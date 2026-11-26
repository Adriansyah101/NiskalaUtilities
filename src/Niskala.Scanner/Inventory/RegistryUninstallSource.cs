using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Niskala.Core.Models;
using Niskala.Interop.Registry;

namespace Niskala.Scanner.Inventory;

public sealed class RegistryUninstallSource(UninstallEntryMapper mapper, ILogger<RegistryUninstallSource> logger)
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly record struct Location(RegistryHive Hive, RegistryView View, InstallScope Scope, RegistryViewKind Kind, string DisplayRoot);

    public IReadOnlyList<InstalledApp> ReadAll(CancellationToken cancellationToken = default)
    {
        var apps = new List<InstalledApp>();
        foreach (Location location in Locations())
        {
            int before = apps.Count;
            foreach (RawUninstallEntry entry in ReadLocation(location, cancellationToken))
            {
                apps.Add(mapper.Map(entry));
            }

            ScannerLog.UninstallLocationRead(logger, location.DisplayRoot, apps.Count - before);
        }

        return apps;
    }

    private static IEnumerable<Location> Locations()
    {
        yield return new(RegistryHive.LocalMachine, RegistryView.Registry64, InstallScope.Machine, RegistryViewKind.Registry64,
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

        if (Environment.Is64BitOperatingSystem)
        {
            yield return new(RegistryHive.LocalMachine, RegistryView.Registry32, InstallScope.Machine, RegistryViewKind.Registry32,
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        }

        yield return new(RegistryHive.CurrentUser, RegistryView.Default, InstallScope.User, RegistryViewKind.Shared,
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private IEnumerable<RawUninstallEntry> ReadLocation(Location location, CancellationToken cancellationToken)
    {
        RegistryKey? root;
        string[] names;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
            root = baseKey.OpenSubKey(UninstallSubKey);
            names = root?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.UninstallKeyOpenFailed(logger, location.DisplayRoot, ex);
            yield break;
        }

        using (root)
        {
            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryReadEntry(root!, name, location) is { } entry)
                {
                    yield return entry;
                }
            }
        }
    }

    private RawUninstallEntry? TryReadEntry(RegistryKey root, string name, Location location)
    {
        string keyPath = $@"{location.DisplayRoot}\{name}";
        try
        {
            using RegistryKey? key = root.OpenSubKey(name);
            if (key is null)
            {
                return null;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string valueName in key.GetValueNames())
            {
                values[valueName] = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }

            return new RawUninstallEntry
            {
                Scope = location.Scope,
                View = location.Kind,
                KeyPath = keyPath,
                KeyName = name,
                LastWriteTimeUtc = RegistryNative.TryGetLastWriteTime(key.Handle, out DateTime written) == 0 ? written : null,
                Values = values,
            };
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.UninstallKeyOpenFailed(logger, keyPath, ex);
            return null;
        }
    }

    private static bool IsAccessError(Exception ex) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException or IOException;
}
