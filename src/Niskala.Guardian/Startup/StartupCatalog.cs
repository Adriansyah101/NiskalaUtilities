using System.Buffers.Binary;
using System.Security;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Interop.Shell;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Startup;

public enum StartupKind
{
    RunValue,
    StartupFolder,
}

public sealed record StartupLocation(StartupKind Kind, InstallScope Scope, bool Is32Bit, string Source, string ApprovedKey);

public sealed record StartupEntry(StartupLocation Location, string Name, string Command, string? TargetPath, bool Enabled, DateTimeOffset? DisabledAt)
{
    public string Path => Location.Kind == StartupKind.StartupFolder ? System.IO.Path.Combine(Location.Source, Name) : Location.Source;
}

public sealed class StartupCatalog(IReadOnlyList<StartupLocation> locations, LeftoverCleaner cleaner, ICommandEnvironment environment, TimeProvider? time = null)
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const int ApprovedLength = 12;
    private const byte EnabledFlag = 0x02;
    private const byte DisabledFlag = 0x03;
    private const int ErrorAccessDenied = 5;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static IReadOnlyList<StartupLocation> DefaultLocations { get; } =
    [
        new(StartupKind.RunValue, InstallScope.User, false, @"HKCU\" + Run, $@"HKCU\{Approved}\Run"),
        new(StartupKind.RunValue, InstallScope.Machine, false, @"HKLM\" + Run, $@"HKLM\{Approved}\Run"),
        new(StartupKind.RunValue, InstallScope.Machine, true, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{Approved}\Run32"),
        new(StartupKind.StartupFolder, InstallScope.User, false, Environment.GetFolderPath(Environment.SpecialFolder.Startup), $@"HKCU\{Approved}\StartupFolder"),
        new(StartupKind.StartupFolder, InstallScope.Machine, false, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), $@"HKLM\{Approved}\StartupFolder"),
    ];

    public IReadOnlyList<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();
        foreach (StartupLocation location in locations)
        {
            using RegistryKey? approved = Open(location.ApprovedKey, writable: false);
            IEnumerable<(string Name, string Command, string? Target)> items = location.Kind == StartupKind.RunValue ? RunValues(location) : FolderItems(location);
            foreach ((string name, string command, string? target) in items)
            {
                (bool enabled, DateTimeOffset? disabledAt) = State(approved?.GetValue(name) as byte[]);
                entries.Add(new StartupEntry(location, name, command, target, enabled, disabledAt));
            }
        }

        return entries;
    }

    public static byte[] ApprovedData(bool enabled, DateTimeOffset when)
    {
        byte[] data = new byte[ApprovedLength];
        data[0] = enabled ? EnabledFlag : DisabledFlag;
        if (!enabled)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), when.ToFileTime());
        }

        return data;
    }

    public int SetEnabled(StartupEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        byte[] data = ApprovedData(enabled, _time.GetUtcNow());

        try
        {
            using RegistryKey? key = Open(entry.Location.ApprovedKey, writable: true);
            if (key is null)
            {
                return ErrorAccessDenied;
            }

            key.SetValue(entry.Name, data, RegistryValueKind.Binary);
            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return ErrorAccessDenied;
        }
        catch (IOException ex)
        {
            return ex.HResult & 0xFFFF;
        }
    }

    public Task<CleanResult> RemoveAsync(StartupEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var app = new InstalledApp
        {
            Id = "startup:" + entry.Name,
            Sources = AppSources.None,
            Scope = entry.Location.Scope,
            DisplayName = entry.Name,
        };

        LeftoverItem item = entry.Location.Kind == StartupKind.RunValue
            ? new LeftoverItem(LeftoverKind.RegistryValue, entry.Location.Source, entry.Name, 100, ConfidenceTier.Safe, [])
            : new LeftoverItem(LeftoverKind.File, entry.Path, null, 100, ConfidenceTier.Safe, []);
        var fingerprint = new Fingerprint { AppId = app.Id, TakenAt = _time.GetUtcNow() };
        return cleaner.CleanAsync(app, fingerprint, [item], new CleanOptions { CreateRestorePoint = false }, null, cancellationToken);
    }

    private static (bool Enabled, DateTimeOffset? DisabledAt) State(byte[]? data)
    {
        if (data is not { Length: > 0 } || (data[0] & 1) == 0)
        {
            return (true, null);
        }

        long fileTime = data.Length >= ApprovedLength ? BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4)) : 0;
        return (false, fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null);
    }

    private IEnumerable<(string Name, string Command, string? Target)> RunValues(StartupLocation location)
    {
        using RegistryKey? key = Open(location.Source, writable: false);
        if (key is null)
        {
            yield break;
        }

        foreach (string name in key.GetValueNames())
        {
            if (name.Length > 0 && key.GetValue(name) is string command && command.Length > 0)
            {
                yield return (name, command, CommandLineParser.Parse(command, environment).Executable);
            }
        }
    }

    private static IEnumerable<(string Name, string Command, string? Target)> FolderItems(StartupLocation location)
    {
        if (!Directory.Exists(location.Source))
        {
            return [];
        }

        var files = new DirectoryInfo(location.Source).EnumerateFiles()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(f => f.FullName)
            .ToList();
        Dictionary<string, ShortcutTarget> links = ShortcutNative.Read(files.Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(l => l.LinkPath, StringComparer.OrdinalIgnoreCase);
        return files.Select(file => links.TryGetValue(file, out ShortcutTarget? link)
            ? (Path.GetFileName(file), $"{link.Path} {link.Arguments}".Trim(), link.Path)
            : (Path.GetFileName(file), file, (string?)file));
    }

    private static RegistryKey? Open(string path, bool writable)
    {
        if (RegistryPath.TryParse(path, RegistryView.Default, out RegistryPath? parsed) != RegistryVerdict.Allowed)
        {
            return null;
        }

        RegistryHive hive = parsed!.Root == RegistryRoot.Machine ? RegistryHive.LocalMachine : parsed.UserSid is null ? RegistryHive.CurrentUser : RegistryHive.Users;
        string subKey = string.Join('\\', parsed.UserSid is null ? parsed.Segments : [parsed.UserSid, .. parsed.Segments]);
        RegistryView view = parsed.View == RegistryView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
        return writable ? root.CreateSubKey(subKey, writable: true) : root.OpenSubKey(subKey);
    }
}
