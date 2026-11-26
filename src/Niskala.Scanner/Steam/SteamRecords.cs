using System.Globalization;
using Microsoft.Win32;
using Niskala.Core.Safety.Registry;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Leftovers;

namespace Niskala.Scanner.Steam;

public sealed record SteamLocations(string ClientFolder)
{
    public string AppsKey { get; init; } = @"HKCU\Software\Valve\Steam\Apps";

    public IReadOnlyList<string> UninstallKeys { get; init; } =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public string TilePropertiesKey { get; init; } = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Start\TileProperties";

    public IReadOnlyList<string> ShortcutFolders { get; init; } =
    [
        .. new[]
        {
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms,
        }
        .Select(Environment.GetFolderPath)
        .Where(p => p.Length > 0),
    ];
}

public sealed record SteamRecord(LeftoverKind Kind, string Path, string Detail, bool PersonalData = false);

public static class SteamRecords
{
    private const string RunGame = SteamLibrary.RunGameUrl;

    private const int SteamClientAppId = 7;
    private const int SteamScreenshotsAppId = 760;

    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public static IReadOnlyList<SteamRecord> Find(int appId, SteamLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (appId is <= 0 or SteamClientAppId or SteamScreenshotsAppId)
        {
            return [];
        }

        string id = appId.ToString(CultureInfo.InvariantCulture);
        var records = new List<SteamRecord>();
        foreach (string library in SteamLibrary.Libraries(locations.ClientFolder))
        {
            string apps = Path.Combine(library, "steamapps");
            AddFolder(records, Path.Combine(apps, "shadercache", id), $"shader cache of app {id}");
            AddFolder(records, Path.Combine(apps, "workshop", "content", id), $"Workshop content of app {id}");
            AddFile(records, Path.Combine(apps, "workshop", $"appworkshop_{id}.acf"), $"Workshop manifest of app {id}");
            AddFolder(records, Path.Combine(apps, "downloading", id), $"unfinished download of app {id}");
            AddFolder(records, Path.Combine(apps, "temp", id), $"update staging of app {id}");
        }

        LibraryCache(records, locations.ClientFolder, id);
        UserData(records, locations.ClientFolder, id);
        AddKey(records, $@"{locations.AppsKey}\{id}", $"Steam's install state of app {id}");
        foreach (string uninstall in locations.UninstallKeys)
        {
            AddKey(records, $@"{uninstall}\{SteamLibrary.KeyPrefix}{id}", $"Uninstall entry of Steam app {id}");
        }

        Shortcuts(records, locations, id);
        AddKey(records, $@"{locations.TilePropertiesKey}\W~{RunGame}{id}", "Start menu state of the game's shortcut");
        return records;
    }

    private static void LibraryCache(List<SteamRecord> records, string client, string id)
    {
        string cache = Path.Combine(client, "appcache", "librarycache");
        if (!Directory.Exists(cache))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(cache, $"{id}*"))
        {
            string name = Path.GetFileName(entry);
            if (name.Equals(id, StringComparison.Ordinal))
            {
                AddFolder(records, entry, $"library art of app {id}");
            }
            else if (name.Length > id.Length && name[id.Length] is '_' or '.')
            {
                AddFile(records, entry, $"library art of app {id}");
            }
        }
    }

    private static void UserData(List<SteamRecord> records, string client, string id)
    {
        string userData = Path.Combine(client, "userdata");
        if (!Directory.Exists(userData))
        {
            return;
        }

        foreach (string account in Directory.EnumerateDirectories(userData))
        {
            string name = Path.GetFileName(account);
            if (name.Length > 0 && name.All(char.IsAsciiDigit))
            {
                string folder = Path.Combine(account, id);
                if (Directory.Exists(folder))
                {
                    records.Add(new SteamRecord(LeftoverKind.Folder, folder, $"Steam Cloud saves and settings of app {id} for account {name}", PersonalData: true));
                }
            }
        }
    }

    private static void Shortcuts(List<SteamRecord> records, SteamLocations locations, string id)
    {
        string icons = Path.Combine(locations.ClientFolder, "steam", "games") + Path.DirectorySeparatorChar;
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in locations.ShortcutFolders.Where(Directory.Exists))
        {
            foreach (string shortcut in Directory.EnumerateFiles(folder, "*.url", Recursive))
            {
                if (InternetShortcut.Read(shortcut) is not { } entry)
                {
                    continue;
                }

                string url = entry.Url;
                string? icon = entry.IconFile;
                bool game = url.Equals(RunGame + id, StringComparison.OrdinalIgnoreCase);
                if (game)
                {
                    AddFile(records, shortcut, $"shortcut that starts {RunGame}{id}");
                }

                if (icon is not null)
                {
                    (game ? ours : others).Add(icon);
                }
            }
        }

        foreach (string icon in ours.Where(i => !others.Contains(i) && i.StartsWith(icons, StringComparison.OrdinalIgnoreCase)))
        {
            AddFile(records, icon, $"icon Steam made for the shortcut of app {id}");
        }
    }

    private static void AddFolder(List<SteamRecord> records, string path, string detail)
    {
        if (Directory.Exists(path))
        {
            records.Add(new SteamRecord(LeftoverKind.Folder, path, detail));
        }
    }

    private static void AddFile(List<SteamRecord> records, string path, string detail)
    {
        if (File.Exists(path))
        {
            records.Add(new SteamRecord(LeftoverKind.File, path, detail));
        }
    }

    private static void AddKey(List<SteamRecord> records, string path, string detail)
    {
        try
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(path);
            if (key is not null)
            {
                records.Add(new SteamRecord(LeftoverKind.RegistryKey, path, detail));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            records.Add(new SteamRecord(LeftoverKind.RegistryKey, path, detail));
        }
    }
}
