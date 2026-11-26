using System.Xml;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Settings;
using Niskala.Guardian.Startup;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;

namespace Niskala.Optimizer.Resources;

public sealed record RunOnceLocation(string Key, InstallScope Scope);

public sealed record ResourceLocations
{
    public const string BlockedSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private const string RunOnceSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    public IReadOnlyList<StartupLocation> Startup { get; init; } = StartupCatalog.DefaultLocations;

    public IReadOnlyList<RunOnceLocation> RunOnce { get; init; } =
    [
        new(@"HKCU\" + RunOnceSubKey, InstallScope.User),
        new(@"HKLM\" + RunOnceSubKey, InstallScope.Machine),
        new(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", InstallScope.Machine),
    ];

    public string ServicesKey { get; init; } = @"HKLM\SYSTEM\CurrentControlSet\Services";

    public IReadOnlyList<string> ClassesRoots { get; init; } = [@"HKCU\Software\Classes", @"HKLM\SOFTWARE\Classes"];

    public IReadOnlyList<string> BlockedKeys { get; init; } = [@"HKLM\" + BlockedSubKey, @"HKCU\" + BlockedSubKey];

    public string BlockTarget { get; init; } = @"HKLM\" + BlockedSubKey;
}

public interface ITaskSource
{
    TaskEnumeration Enumerate();
}

public sealed class SystemTaskSource : ITaskSource
{
    public static SystemTaskSource Instance { get; } = new();

    public TaskEnumeration Enumerate() => TaskSchedulerNative.Enumerate();
}

internal static class StartupSource
{
    public static IEnumerable<ResourceItem> List(StartupCatalog catalog, IReadOnlyList<RunOnceLocation> runOnce, IProgramFacts programs, ICommandEnvironment environment)
    {
        foreach (StartupEntry entry in catalog.List())
        {
            bool folder = entry.Location.Kind == StartupKind.StartupFolder;
            ProgramInfo? program = entry.TargetPath is { } target ? programs.Describe(target) : null;
            yield return new ResourceItem
            {
                Id = $"{(folder ? "folder" : "run")}|{entry.Location.Source}|{entry.Name}",
                Kind = folder ? ResourceKind.StartupFolder : ResourceKind.RunEntry,
                Name = folder ? Path.GetFileNameWithoutExtension(entry.Name) : entry.Name,
                Location = entry.Location.Source,
                Target = entry.Location.ApprovedKey,
                ValueName = entry.Name,
                Command = entry.Command,
                Program = entry.TargetPath,
                Publisher = program?.Publisher,
                Scope = entry.Location.Scope,
                Enabled = entry.Enabled,
                RunsAtSignIn = true,
                Impact = BootImpactEstimator.Estimate(folder ? ResourceKind.StartupFolder : ResourceKind.RunEntry, entry.Enabled, program),
            };
        }

        foreach (RunOnceLocation location in runOnce)
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(location.Key);
            foreach (string name in key?.GetValueNames() ?? [])
            {
                if (name.Length == 0 || key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command || command.Length == 0)
                {
                    continue;
                }

                string? executable = CommandLineParser.Parse(command, environment).Executable;
                ProgramInfo? program = executable is null ? null : programs.Describe(executable);
                yield return RunOnceItem(location, name, command, executable, program, enabled: true, parked: null);
            }
        }
    }

    public static ResourceItem RunOnceItem(RunOnceLocation location, string name, string command, string? executable, ProgramInfo? program, bool enabled, RawRegistryValue? parked) => new()
    {
        Id = $"runonce|{location.Key}|{name}",
        Kind = ResourceKind.RunOnceEntry,
        Name = name.TrimStart('!', '*'),
        Location = location.Key,
        Target = location.Key,
        ValueName = name,
        Command = command,
        Program = executable,
        Publisher = program?.Publisher,
        Scope = location.Scope,
        Enabled = enabled,
        RunsAtSignIn = true,
        ParkedValue = parked,
        Impact = BootImpactEstimator.Estimate(ResourceKind.RunOnceEntry, enabled, program),
    };
}

internal static class ServiceSource
{
    private const int Win32Types = 0x30;
    private const int UserService = 0x40;
    private const int DisabledStart = 4;

    public static IEnumerable<ResourceItem> List(string servicesKey, RegistryGuard guard, IProgramFacts programs, string windowsDirectory)
    {
        using RegistryKey? services = RegistryPath.OpenReadOnly(servicesKey);
        foreach (string name in services?.GetSubKeyNames() ?? [])
        {
            using RegistryKey? service = Open(services!, name);
            if (service?.GetValue("Type") is not int type || (type & Win32Types) == 0 || (type & UserService) != 0
                || service.GetValue("Start") is not int start
                || ServiceImagePath.Resolve(service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, windowsDirectory) is not { } image
                || PathSyntax.IsSameOrUnder(image, windowsDirectory, out _))
            {
                continue;
            }

            ProgramInfo program = programs.Describe(image);
            if (program.IsMicrosoft)
            {
                continue;
            }

            RegistryDecision decision = guard.CheckKey($@"{SettingsEditor.ServicesKey}{name}");
            bool delayed = service.GetValue("DelayedAutostart") is int flag && flag != 0;
            var startType = (ServiceStartType)start;
            yield return new ResourceItem
            {
                Id = "service|" + name,
                Kind = ResourceKind.Service,
                Name = service.GetValue("DisplayName") is string display && display.Length > 0 && !display.StartsWith('@') ? display : name,
                Location = name,
                Target = name,
                Program = image,
                Command = service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                Publisher = program.Publisher,
                Scope = InstallScope.Machine,
                Enabled = start != DisabledStart,
                StartType = startType,
                DelayedStart = delayed,
                Impact = BootImpactEstimator.Estimate(ResourceKind.Service, start != DisabledStart, program, startType, delayed),
                Blocked = decision.IsAllowed ? null : decision.ToString(),
            };
        }
    }

    private static RegistryKey? Open(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

internal static class TaskSource
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    public static IEnumerable<ResourceItem> List(ITaskSource tasks, IProgramFacts programs, ICommandEnvironment environment)
    {
        foreach (ScheduledTaskXml task in tasks.Enumerate().Tasks)
        {
            if (SettingsEditor.IsWindowsTask(task.Path) || Read(task.Xml) is not { Command: { } command } facts)
            {
                continue;
            }

            string program = environment.ExpandVariables(command.Trim('"'));
            ProgramInfo info = programs.Describe(program);
            if (info.IsMicrosoft)
            {
                continue;
            }

            yield return new ResourceItem
            {
                Id = "task|" + task.Path,
                Kind = ResourceKind.ScheduledTask,
                Name = task.Path.TrimStart('\\'),
                Location = task.Path,
                Target = task.Path,
                Command = facts.Arguments is { } arguments ? $"{command} {arguments}" : command,
                Program = program,
                Publisher = info.Publisher,
                Scope = InstallScope.Machine,
                Enabled = facts.Enabled,
                RunsAtSignIn = facts.AtSignIn,
                Impact = BootImpactEstimator.Estimate(ResourceKind.ScheduledTask, facts.Enabled, info, atSignIn: facts.AtSignIn),
            };
        }
    }

    internal static (bool Enabled, bool AtSignIn, string? Command, string? Arguments)? Read(string xml)
    {
        bool enabled = true;
        bool atSignIn = false;
        string? command = null;
        string? arguments = null;
        int depth = -1;
        try
        {
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, Settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Settings":
                        depth = reader.Depth;
                        break;
                    case "Enabled" when reader.Depth == depth + 1:
                        enabled = !string.Equals(reader.ReadElementContentAsString().Trim(), "false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "LogonTrigger" or "BootTrigger":
                        atSignIn = true;
                        break;
                    case "Command" when command is null:
                        command = reader.ReadElementContentAsString().Trim();
                        break;
                    case "Arguments" when arguments is null:
                        arguments = reader.ReadElementContentAsString().Trim();
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        return (enabled, atSignIn, command is { Length: > 0 } ? command : null, arguments is { Length: > 0 } ? arguments : null);
    }
}

internal static class ContextMenuSource
{
    private static readonly string[] Types = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive", "DesktopBackground"];

    public static IEnumerable<ResourceItem> List(ResourceLocations locations, IProgramFacts programs, ICommandEnvironment environment)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in locations.BlockedKeys)
        {
            using RegistryKey? list = RegistryPath.OpenReadOnly(key);
            blocked.UnionWith(list?.GetValueNames() ?? []);
        }

        var found = new Dictionary<string, (string Name, List<string> Types, string Root)>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in locations.ClassesRoots)
        {
            foreach (string type in Types)
            {
                using RegistryKey? handlers = RegistryPath.OpenReadOnly($@"{root}\{type}\shellex\ContextMenuHandlers");
                foreach (string name in handlers?.GetSubKeyNames() ?? [])
                {
                    using RegistryKey? handler = handlers!.OpenSubKey(name);
                    string? clsid = Guid.TryParse(name, out _) ? name : handler?.GetValue(string.Empty) as string;
                    if (clsid is null || !Guid.TryParse(clsid, out Guid parsed))
                    {
                        continue;
                    }

                    string key = parsed.ToString("B").ToUpperInvariant();
                    if (!found.TryGetValue(key, out var entry))
                    {
                        entry = (name, [], root);
                        found[key] = entry;
                    }

                    entry.Types.Add(type);
                }
            }
        }

        foreach ((string clsid, (string name, List<string> types, string root)) in found)
        {
            if (Server(locations.ClassesRoots, clsid, environment) is not { } server || !Path.IsPathFullyQualified(server))
            {
                continue;
            }

            ProgramInfo program = programs.Describe(server);
            if (program.IsMicrosoft)
            {
                continue;
            }

            bool enabled = !blocked.Contains(clsid);
            yield return new ResourceItem
            {
                Id = "shell|" + clsid,
                Kind = ResourceKind.ContextMenu,
                Name = Guid.TryParse(name, out _) ? Path.GetFileNameWithoutExtension(server) : name,
                Location = string.Join(", ", types.Distinct(StringComparer.OrdinalIgnoreCase)),
                Target = locations.BlockTarget,
                ValueName = clsid,
                Program = server,
                Publisher = program.Publisher,
                Scope = root.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? InstallScope.User : InstallScope.Machine,
                Enabled = enabled,
                Impact = BootImpact.None,
            };
        }
    }

    private static string? Server(IReadOnlyList<string> roots, string clsid, ICommandEnvironment environment)
    {
        foreach (string root in roots)
        {
            using RegistryKey? server = RegistryPath.OpenReadOnly($@"{root}\CLSID\{clsid}\InprocServer32");
            if (server?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path && path.Length > 0)
            {
                return environment.ExpandVariables(path.Trim().Trim('"'));
            }
        }

        return null;
    }
}
