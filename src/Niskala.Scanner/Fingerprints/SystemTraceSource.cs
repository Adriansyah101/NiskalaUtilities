using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Msi;
using Niskala.Interop.Shell;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Fingerprints;

public sealed record TaskList(IReadOnlyList<TaskTrace> Tasks, int UnreadableItems, int Error);

public interface ISystemTraceSource
{
    IReadOnlyList<ServiceTrace> Services();

    IReadOnlyList<RegistryValueTrace> RunEntries();

    IReadOnlyList<RegistryValueTrace> AppPaths();

    TaskList ScheduledTasks();

    IReadOnlyList<ShortcutTrace> Shortcuts();

    Guid? FindUpgradeCode(Guid productCode, InstallScope scope);

    bool? IsProductRegistered(Guid productCode);
}

public sealed class SystemTraceSource(ICommandEnvironment environment, ILogger<SystemTraceSource> logger) : ISystemTraceSource
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly RegistryLocation[] RunKeys =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    private static readonly RegistryLocation[] AppPathKeys =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, AppPathsKey, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, AppPathsKey, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
        new(RegistryHive.CurrentUser, RegistryView.Default, AppPathsKey, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
    ];

    private static readonly EnumerationOptions ShortcutOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private readonly record struct RegistryLocation(RegistryHive Hive, RegistryView View, string Path, string Display);

    public IReadOnlyList<ServiceTrace> Services()
    {
        var services = new List<ServiceTrace>();
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? root = baseKey.OpenSubKey(ServicesKey);
            foreach (string name in root?.GetSubKeyNames() ?? [])
            {
                if (ReadService(root!, name) is { } service)
                {
                    services.Add(service);
                }
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceSourceFailed(logger, "services", ex);
        }

        return services;
    }

    public IReadOnlyList<RegistryValueTrace> RunEntries()
    {
        var entries = new List<RegistryValueTrace>();
        foreach (RegistryLocation location in RunKeys)
        {
            Read(location, key =>
            {
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string data)
                    {
                        entries.Add(new RegistryValueTrace(location.Display, name, data));
                    }
                }
            });
        }

        return entries;
    }

    public IReadOnlyList<RegistryValueTrace> AppPaths()
    {
        var entries = new List<RegistryValueTrace>();
        foreach (RegistryLocation location in AppPathKeys)
        {
            Read(location, root =>
            {
                foreach (string name in root.GetSubKeyNames())
                {
                    string display = $@"{location.Display}\{name}";
                    try
                    {
                        using RegistryKey? key = root.OpenSubKey(name);
                        if (key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string executable)
                        {
                            entries.Add(new RegistryValueTrace(display, null, executable));
                        }

                        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path)
                        {
                            entries.Add(new RegistryValueTrace(display, "Path", path));
                        }
                    }
                    catch (Exception ex) when (IsAccessError(ex))
                    {
                        ScannerLog.TraceKeyUnreadable(logger, display, ex);
                    }
                }
            });
        }

        return entries;
    }

    public TaskList ScheduledTasks()
    {
        TaskEnumeration enumeration = TaskSchedulerNative.Enumerate();
        if (enumeration.Error != 0)
        {
            ScannerLog.TaskSchedulerUnavailable(logger, enumeration.Error);
        }

        List<TaskTrace> actions = [.. enumeration.Tasks.SelectMany(task => TaskXml.ReadExecActions(task.Path, task.Xml))];
        return new TaskList(actions, enumeration.UnreadableItems, enumeration.Error);
    }

    public IReadOnlyList<ShortcutTrace> Shortcuts()
    {
        var links = new List<string>();
        foreach (string folder in ShortcutFolders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var enumeration = new FileSystemEnumerable<string>(folder, static (ref FileSystemEntry entry) => entry.ToFullPath(), ShortcutOptions)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory && entry.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
                ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            links.AddRange(enumeration);
        }

        return [.. ShortcutNative.Read(links.Distinct(StringComparer.OrdinalIgnoreCase)).Select(target => new ShortcutTrace(
            target.LinkPath,
            target.Path,
            target.Arguments,
            Guid.TryParse(target.MsiProductCode, out Guid product) ? product : null))];
    }

    public Guid? FindUpgradeCode(Guid productCode, InstallScope scope)
    {
        (RegistryHive hive, string path) = scope == InstallScope.Machine
            ? (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\UpgradeCodes")
            : (RegistryHive.CurrentUser, @"Software\Microsoft\Installer\UpgradeCodes");
        string packedProduct = MsiPackedGuid.Pack(productCode);
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? root = baseKey.OpenSubKey(path);
            foreach (string packedUpgrade in root?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? key = root!.OpenSubKey(packedUpgrade);
                if (key is not null
                    && key.GetValueNames().Contains(packedProduct, StringComparer.OrdinalIgnoreCase)
                    && MsiPackedGuid.TryUnpack(packedUpgrade, out Guid upgradeCode))
                {
                    return upgradeCode;
                }
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceSourceFailed(logger, "upgrade codes", ex);
        }

        return null;
    }

    public bool? IsProductRegistered(Guid productCode) => MsiNative.QueryProductState(productCode.ToString("B")) switch
    {
        MsiProductState.NotRegistered => false,
        MsiProductState.Other => null,
        _ => true,
    };

    private ServiceTrace? ReadService(RegistryKey root, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(name);
            string? stored = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (key is null || string.IsNullOrWhiteSpace(stored))
            {
                return null;
            }

            string? image = ServiceImagePath.Resolve(stored, environment.WindowsDirectory);
            string? serviceDll = null;
            if (image is not null && Path.GetFileName(image).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                using RegistryKey? parameters = key.OpenSubKey("Parameters");
                if (parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string dll)
                {
                    serviceDll = environment.ExpandVariables(dll.Trim());
                }
            }

            return new ServiceTrace(name, key.GetValue("DisplayName") as string, image ?? stored, serviceDll, key.GetValue("Start") as int?);
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceKeyUnreadable(logger, $@"HKLM\{ServicesKey}\{name}", ex);
            return null;
        }
    }

    private void Read(RegistryLocation location, Action<RegistryKey> read)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using RegistryKey? key = baseKey.OpenSubKey(location.Path);
            if (key is not null)
            {
                read(key);
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceKeyUnreadable(logger, location.Display, ex);
        }
    }

    private static IEnumerable<string> ShortcutFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned");
    }

    private static bool IsAccessError(Exception ex) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException or IOException;
}
