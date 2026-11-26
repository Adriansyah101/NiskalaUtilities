namespace Niskala.Core.Safety;

public static class DefaultProtectedLocations
{
    private static readonly string[] DriveRootSystemEntries =
    [
        "$Recycle.Bin", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "$GetCurrent",
        "Boot", "Config.Msi", "EFI", "PerfLogs", "Recovery", "System Volume Information", "Windows.old", "inetpub",
        "bootmgr", "BOOTNXT", "BOOTSECT.BAK", "DumpStack.log", "DumpStack.log.tmp",
        "hiberfil.sys", "pagefile.sys", "swapfile.sys",
    ];

    private static readonly string[] ProgramFilesSystemChildren =
    [
        "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer",
        "Microsoft Update Health Tools", "Reference Assemblies", "Uninstall Information",
    ];

    private static readonly string[] ProgramFilesBrowserRuntime =
    [
        @"Microsoft\Edge", @"Microsoft\EdgeCore", @"Microsoft\EdgeUpdate", @"Microsoft\EdgeWebView",
    ];

    private static readonly string[] CommonFilesSystemChildren = ["microsoft shared", "System", "Services", "SpeechEngines"];

    private static readonly string[] ProgramDataSystemChildren =
    [
        "Microsoft", "Packages", "USOShared", "USOPrivate", "regid.1991-06.com.microsoft", "ssh",
    ];

    private static readonly string[] ProfileKnownFolders =
    [
        "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "Favorites", "Links",
        "Saved Games", "Contacts", "Searches", "OneDrive", "3D Objects",
    ];

    private static readonly string[] StartMenuSystemGroups =
    [
        "Accessibility", "Accessories", "Administrative Tools", "Maintenance", "System Tools",
        "Windows Accessories", "Windows Administrative Tools", "Windows Ease of Access", "Windows PowerShell",
        "Windows System", "Windows Tools",
    ];

    public static IEnumerable<ProtectedLocation> Build(MachineLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        yield return Tree(layout.WindowsDirectory, "Windows directory");

        string prefetch = Path.Combine(layout.WindowsDirectory, "Prefetch");
        yield return Self(prefetch, "Prefetch folder");
        yield return Tree(Path.Combine(prefetch, "ReadyBoot"), "ReadyBoot trace");
        yield return Tree(Path.Combine(prefetch, "Layout.ini"), "boot layout of the disk optimizer");

        foreach (string drive in layout.DriveRoots)
        {
            foreach (string name in DriveRootSystemEntries)
            {
                yield return Tree(Path.Combine(drive, name), "system entry at drive root");
            }
        }

        foreach (string programFiles in layout.ProgramFilesDirectories)
        {
            yield return Self(programFiles, "Program Files root");
            foreach (string name in ProgramFilesSystemChildren)
            {
                yield return Tree(Path.Combine(programFiles, name), "Windows component");
            }

            foreach (string name in ProgramFilesBrowserRuntime)
            {
                yield return Tree(Path.Combine(programFiles, name), "Windows browser and WebView2 runtime");
            }
        }

        foreach (string commonFiles in layout.CommonProgramFilesDirectories)
        {
            yield return Self(commonFiles, "Common Files root");
            foreach (string name in CommonFilesSystemChildren)
            {
                yield return Tree(Path.Combine(commonFiles, name), "Windows shared component");
            }
        }

        string programData = layout.ProgramDataDirectory;
        yield return Self(programData, "ProgramData root");
        foreach (string name in ProgramDataSystemChildren)
        {
            yield return Tree(Path.Combine(programData, name), "Windows data");
        }

        yield return Self(Path.Combine(programData, "Package Cache"), "installer package cache");
        foreach (ProtectedLocation rule in StartMenuPrograms(Path.Combine(programData, @"Microsoft\Windows\Start Menu\Programs")))
        {
            yield return rule;
        }

        if (layout.ProfilesDirectory is { } profilesDirectory)
        {
            yield return Self(profilesDirectory, "profiles root");
        }

        if (layout.DefaultProfile is { } defaultProfile)
        {
            yield return Tree(defaultProfile, "template profile for new accounts");
        }

        if (layout.PublicProfile is { } publicProfile)
        {
            yield return Self(publicProfile, "public profile");
            foreach (string name in ProfileKnownFolders)
            {
                yield return Self(Path.Combine(publicProfile, name), "public known folder");
            }
        }

        foreach (string profile in layout.UserProfiles)
        {
            foreach (ProtectedLocation rule in UserProfile(profile))
            {
                yield return rule;
            }
        }

        foreach (string folder in layout.CurrentUserKnownFolders)
        {
            yield return Self(folder, "known folder");
        }

        foreach (string folder in layout.ApplicationDirectories)
        {
            yield return Tree(folder, "Niskala program or backup data");
        }
    }

    private static IEnumerable<ProtectedLocation> UserProfile(string profile)
    {
        yield return Self(profile, "user profile");
        yield return new ProtectedLocation(profile, ProtectionKind.Tree, "user registry hive", ChildNamePrefix: "ntuser.");

        string appData = Path.Combine(profile, "AppData");
        string roaming = Path.Combine(appData, "Roaming");
        string local = Path.Combine(appData, "Local");

        yield return Self(appData, "AppData root");
        yield return Self(roaming, "AppData root");
        yield return Self(local, "AppData root");
        yield return Self(Path.Combine(appData, "LocalLow"), "AppData root");
        yield return Self(Path.Combine(local, "Programs"), "per-user programs root");
        yield return Self(Path.Combine(local, "Temp"), "temp root");
        yield return Self(Path.Combine(local, "Packages"), "packaged app data root");
        yield return Self(Path.Combine(local, "Microsoft"), "Microsoft app data root");
        yield return Tree(Path.Combine(local, @"Microsoft\Windows"), "Windows shell data");
        yield return Tree(Path.Combine(local, @"Microsoft\WindowsApps"), "app execution aliases");
        yield return Self(Path.Combine(roaming, "Microsoft"), "Microsoft app data root");
        yield return Tree(Path.Combine(roaming, @"Microsoft\Windows"), "Windows shell data");

        foreach (ProtectedLocation rule in StartMenuPrograms(Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs")))
        {
            yield return rule;
        }

        foreach (string name in ProfileKnownFolders)
        {
            yield return Self(Path.Combine(profile, name), "known folder");
        }
    }

    private static IEnumerable<ProtectedLocation> StartMenuPrograms(string programs)
    {
        yield return Self(programs, "Start Menu programs root");
        yield return Self(Path.Combine(programs, "Startup"), "Startup folder");
        foreach (string name in StartMenuSystemGroups)
        {
            yield return Tree(Path.Combine(programs, name), "Windows Start Menu group");
        }
    }

    private static ProtectedLocation Tree(string path, string reason) => new(path, ProtectionKind.Tree, reason);

    private static ProtectedLocation Self(string path, string reason) => new(path, ProtectionKind.Self, reason);
}
