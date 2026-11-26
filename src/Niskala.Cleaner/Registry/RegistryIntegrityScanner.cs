using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Firewall;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cleaner.Registry;

public enum IntegrityIssueKind
{
    FileAssociation,
    SharedDll,
    ComRegistration,
    FirewallRule,
    ServicePath,
}

public sealed record IntegrityIssue(IntegrityIssueKind Kind, LeftoverKind ItemKind, string Path, string? ValueName, string Target, string Detail)
{
    public string? Protection { get; init; }

    public bool Fixable => Protection is null;

    public bool MachineWide => ItemKind is LeftoverKind.FirewallRule || Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
}

public sealed record IntegrityReport(IReadOnlyList<IntegrityIssue> Issues, TimeSpan Elapsed)
{
    public IReadOnlyList<IntegrityIssue> Fixable => [.. Issues.Where(i => i.Fixable)];
}

public sealed record IntegrityLocations(IReadOnlyList<string> ClassesRoots, IReadOnlyList<string> SharedDllKeys, string? ServicesKey)
{
    public static IntegrityLocations Default { get; } = new(
        [@"HKCU\Software\Classes", @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\WOW6432Node\Classes"],
        [@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"],
        @"HKLM\SYSTEM\CurrentControlSet\Services");
}

public sealed class RegistryIntegrityScanner(
    IntegrityLocations locations,
    RegistryGuard guard,
    IFirewallRuleSource firewall,
    ICommandEnvironment environment,
    ILogger<RegistryIntegrityScanner> logger)
{
    private const string ShellKey = "shell";
    private const string CommandKey = "command";
    private const string ApplicationsKey = "Applications";
    private const string ClsidKey = "CLSID";
    private const int ServiceWin32Types = 0x30;
    private const int ServiceUserTemplate = 0xC0;

    private const string InprocServer = "InprocServer32";
    private const string LocalServer = "LocalServer32";

    private static readonly HashSet<string> NonTypeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ClsidKey, "Interface", "TypeLib", "AppID", "Installer", "MIME", "SystemFileAssociations", "Local Settings", "PackagedCom",
        "ActivatableClasses", "Extensions", "WOW6432Node", "Component Categories", "DirectShow", "Media Type", "MediaFoundation", "Protocols",
        "PROTOCOLS", "Filter", "Unknown", "AllFilesystemObjects", ApplicationsKey,
    };

    public static IReadOnlyList<IntegrityIssueKind> AllKinds { get; } = Enum.GetValues<IntegrityIssueKind>();

    public IntegrityReport Scan(IReadOnlyCollection<IntegrityIssueKind>? kinds = null, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        kinds ??= AllKinds;
        var issues = new List<IntegrityIssue>();
        if (kinds.Contains(IntegrityIssueKind.FileAssociation))
        {
            issues.AddRange(FileAssociations(cancellationToken));
        }

        if (kinds.Contains(IntegrityIssueKind.SharedDll))
        {
            issues.AddRange(SharedDlls());
        }

        if (kinds.Contains(IntegrityIssueKind.ComRegistration))
        {
            issues.AddRange(ComRegistrations(cancellationToken));
        }

        if (kinds.Contains(IntegrityIssueKind.FirewallRule))
        {
            issues.AddRange(FirewallRules());
        }

        if (kinds.Contains(IntegrityIssueKind.ServicePath))
        {
            issues.AddRange(ServicePaths());
        }

        IReadOnlyList<IntegrityIssue> guarded = [.. issues.Select(Guarded)];
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        CleanerLog.IntegrityScanned(logger, guarded.Count, elapsed);
        return new IntegrityReport(guarded, elapsed);
    }

    public bool IsStillBroken(IntegrityIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Kind switch
        {
            IntegrityIssueKind.FileAssociation when issue.ItemKind == LeftoverKind.RegistryValue =>
                ReadDefault(issue.Path) is { Length: > 0 } progId && progId.Equals(issue.Target, StringComparison.OrdinalIgnoreCase) && !ProgIdExists(progId),
            IntegrityIssueKind.FileAssociation => ReadDefault(issue.Path + @"\" + CommandKey) is { } command && MissingProgram(command, IsWow(issue.Path)) is not null,
            IntegrityIssueKind.SharedDll => ValueExists(issue.Path, issue.ValueName!) && IsMissing(issue.ValueName!, IsWow(issue.Path)),
            IntegrityIssueKind.ComRegistration => MissingServer(issue.Path, IsWow(issue.Path)) is not null,
            IntegrityIssueKind.FirewallRule => FirewallRules().Any(i => i.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase)),
            _ => MissingImage(ReadString(issue.Path, "ImagePath")) is not null,
        };
    }

    private IntegrityIssue Guarded(IntegrityIssue issue)
    {
        RegistryDecision? decision = issue.ItemKind switch
        {
            LeftoverKind.RegistryKey => guard.CheckKey(issue.Path),
            LeftoverKind.RegistryValue => guard.CheckValue(issue.Path, issue.ValueName),
            _ => null,
        };
        return decision is { IsAllowed: false } ? issue with { Protection = decision.ToString() } : issue;
    }

    private IEnumerable<IntegrityIssue> FileAssociations(CancellationToken cancellationToken)
    {
        foreach (string root in locations.ClassesRoots)
        {
            using RegistryKey? classes = RegistryPath.OpenReadOnly(root);
            if (classes is null)
            {
                continue;
            }

            bool wow = IsWow(root);
            foreach (string name in classes.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name.StartsWith('.'))
                {
                    if (ReadDefault(classes, name) is { Length: > 0 } progId && !ProgIdExists(progId))
                    {
                        yield return new IntegrityIssue(
                            IntegrityIssueKind.FileAssociation, LeftoverKind.RegistryValue, $@"{root}\{name}", string.Empty, progId,
                            $"{name} files open with {progId}, which is not registered");
                    }

                    continue;
                }

                if (NonTypeKeys.Contains(name) || name.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (IntegrityIssue issue in BrokenVerbs(classes, root, name, wow))
                {
                    yield return issue;
                }
            }

            using RegistryKey? applications = classes.OpenSubKey(ApplicationsKey);
            foreach (string name in applications?.GetSubKeyNames() ?? [])
            {
                foreach (IntegrityIssue issue in BrokenVerbs(classes, root, $@"{ApplicationsKey}\{name}", wow))
                {
                    yield return issue;
                }
            }
        }
    }

    private IEnumerable<IntegrityIssue> BrokenVerbs(RegistryKey classes, string root, string type, bool wow)
    {
        using RegistryKey? shell = OpenSubKey(classes, $@"{type}\{ShellKey}");
        if (shell is null)
        {
            yield break;
        }

        foreach (string verb in shell.GetSubKeyNames())
        {
            if (ReadDefault(shell, $@"{verb}\{CommandKey}") is { } command && MissingProgram(command, wow) is { } missing)
            {
                yield return new IntegrityIssue(
                    IntegrityIssueKind.FileAssociation, LeftoverKind.RegistryKey, $@"{root}\{type}\{ShellKey}\{verb}", null, missing,
                    $"\"{verb}\" on {type} runs {missing}, which is gone");
            }
        }
    }

    private IEnumerable<IntegrityIssue> SharedDlls()
    {
        foreach (string keyPath in locations.SharedDllKeys)
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(keyPath);
            foreach (string name in key?.GetValueNames() ?? [])
            {
                if (name.Length > 0 && IsMissing(name, IsWow(keyPath)))
                {
                    yield return new IntegrityIssue(IntegrityIssueKind.SharedDll, LeftoverKind.RegistryValue, keyPath, name, name, "a shared file reference to a file that is gone");
                }
            }
        }
    }

    private IEnumerable<IntegrityIssue> ComRegistrations(CancellationToken cancellationToken)
    {
        foreach (string root in locations.ClassesRoots)
        {
            using RegistryKey? clsids = RegistryPath.OpenReadOnly($@"{root}\{ClsidKey}");
            if (clsids is null)
            {
                continue;
            }

            bool wow = IsWow(root);
            foreach (string clsid in clsids.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(clsid, out _))
                {
                    continue;
                }

                using RegistryKey? entry = OpenSubKey(clsids, clsid);
                if (entry is not null && MissingServer(entry, wow) is { } missing)
                {
                    yield return new IntegrityIssue(
                        IntegrityIssueKind.ComRegistration, LeftoverKind.RegistryKey, $@"{root}\{ClsidKey}\{clsid}", null, missing,
                        $"COM class {ReadDefault(entry, string.Empty) ?? clsid} is served by {missing}, which is gone");
                }
            }
        }
    }

    private IEnumerable<IntegrityIssue> FirewallRules()
    {
        int error = firewall.TryList(out List<FirewallRule> rules);
        if (error != 0)
        {
            CleanerLog.IntegrityUnreadable(logger, IntegrityIssueKind.FirewallRule, "firewall", error);
            yield break;
        }

        foreach (IGrouping<string, FirewallRule> named in rules.Where(r => r.Name.Length > 0).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (named.All(r => r.ApplicationName is { Length: > 0 } app && IsMissing(app, wow: false)))
            {
                string program = named.First().ApplicationName!;
                yield return new IntegrityIssue(
                    IntegrityIssueKind.FirewallRule, LeftoverKind.FirewallRule, named.Key, null, program,
                    $"firewall rule for {program}, which is gone");
            }
        }
    }

    private IEnumerable<IntegrityIssue> ServicePaths()
    {
        if (locations.ServicesKey is not { } servicesKey)
        {
            yield break;
        }

        using RegistryKey? services = RegistryPath.OpenReadOnly(servicesKey);
        foreach (string name in services?.GetSubKeyNames() ?? [])
        {
            using RegistryKey? service = OpenSubKey(services!, name);
            if (service?.GetValue("Type") is not int type || (type & ServiceWin32Types) == 0 || (type & ServiceUserTemplate) != 0)
            {
                continue;
            }

            if (MissingImage(service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string) is { } missing)
            {
                yield return new IntegrityIssue(
                    IntegrityIssueKind.ServicePath, LeftoverKind.RegistryKey, $@"{servicesKey}\{name}", null, missing,
                    $"service {name} starts {missing}, which is gone");
            }
        }
    }

    private string? MissingServer(string keyPath, bool wow)
    {
        using RegistryKey? entry = RegistryPath.OpenReadOnly(keyPath);
        return entry is null ? null : MissingServer(entry, wow);
    }

    private string? MissingServer(RegistryKey entry, bool wow)
    {
        string? missing = null;
        foreach (string server in (ReadOnlySpan<string>)[InprocServer, LocalServer])
        {
            if (ReadDefault(entry, server) is not { Length: > 0 } value)
            {
                continue;
            }

            string? gone = server == InprocServer ? (IsMissing(value, wow) ? Clean(value) : null) : MissingProgram(value, wow);
            if (gone is null)
            {
                return null;
            }

            missing ??= gone;
        }

        return missing;
    }

    private string? MissingProgram(string command, bool wow)
    {
        string? executable = CommandLineParser.Parse(command, environment).Executable;
        return executable is not null && IsMissing(executable, wow) ? executable : null;
    }

    private string? MissingImage(string? imagePath) =>
        ServiceImagePath.Resolve(imagePath, environment.WindowsDirectory) is { } image && IsMissing(image, wow: false) ? image : null;

    private bool IsMissing(string raw, bool wow)
    {
        string path = Clean(raw);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || PathSyntax.IsSameOrUnder(path, environment.WindowsDirectory, out _))
        {
            return false;
        }

        if (Path.GetPathRoot(path) is not { Length: 3 } drive || !IsFixedDrive(drive))
        {
            return false;
        }

        foreach (string candidate in Expansions(raw, wow))
        {
            if (environment.FileExists(candidate) || Directory.Exists(candidate))
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerable<string> Expansions(string raw, bool wow)
    {
        string text = raw.Trim().Trim('"');
        yield return environment.ExpandVariables(text);
        if (wow)
        {
            yield return environment.ExpandVariables(text
                .Replace("%ProgramFiles%", "%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase)
                .Replace("%CommonProgramFiles%", "%CommonProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase));
        }
    }

    private string Clean(string raw) => environment.ExpandVariables(raw.Trim().Trim('"').Trim());

    private bool ProgIdExists(string progId) => locations.ClassesRoots.Any(root =>
    {
        using RegistryKey? key = RegistryPath.OpenReadOnly($@"{root}\{progId}");
        return key is not null;
    });

    private static bool IsFixedDrive(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            return info.DriveType == DriveType.Fixed && info.IsReady;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsWow(string path) => path.Contains(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase);

    private static RegistryKey? OpenSubKey(RegistryKey parent, string name)
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

    private static string? ReadDefault(RegistryKey parent, string subKey)
    {
        if (subKey.Length == 0)
        {
            return DefaultOf(parent);
        }

        using RegistryKey? key = OpenSubKey(parent, subKey);
        return key is null ? null : DefaultOf(key);
    }

    private static string? DefaultOf(RegistryKey key) => key.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private static string? ReadDefault(string keyPath)
    {
        using RegistryKey? key = RegistryPath.OpenReadOnly(keyPath);
        return key is null ? null : DefaultOf(key);
    }

    private static string? ReadString(string keyPath, string name)
    {
        using RegistryKey? key = RegistryPath.OpenReadOnly(keyPath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static bool ValueExists(string keyPath, string name)
    {
        using RegistryKey? key = RegistryPath.OpenReadOnly(keyPath);
        return key?.GetValue(name) is not null;
    }
}
