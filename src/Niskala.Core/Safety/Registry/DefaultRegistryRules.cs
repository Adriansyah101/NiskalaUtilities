namespace Niskala.Core.Safety.Registry;

public static class DefaultRegistryRules
{
    private static readonly string[] SystemClasses =
    [
        "*", "AllFilesystemObjects", "Directory", "Drive", "Folder", "DesktopBackground", "LibraryFolder",
        "Network", "Printers", "Unknown", "exefile", "dllfile", "lnkfile", "batfile", "cmdfile", "comfile",
        "regfile", "inifile", "txtfile", "htmlfile", "Msi.Package", "Msi.Patch", "http", "https", "ftp",
        "mailto", "file", "InternetShortcut", "Microsoft.PowerShellScript.1", "VBSFile", "JSFile", "WSFFile",
        "CompressedFolder", "ms-settings",
    ];

    private static readonly string[] SystemExtensions =
    [
        ".bat", ".cmd", ".com", ".cpl", ".dll", ".exe", ".htm", ".html", ".inf", ".ini", ".lnk", ".msc", ".msi",
        ".msp", ".ocx", ".pif", ".ps1", ".reg", ".scr", ".sys", ".txt", ".url", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".xml", ".zip", ".cab", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".tif", ".tiff",
        ".webp", ".heic", ".svg", ".mp3", ".mp4", ".wav", ".wma", ".wmv", ".avi", ".mkv", ".mov", ".pdf", ".rtf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".log", ".json", ".appx", ".msix",
        ".appxbundle", ".msixbundle", ".theme", ".library-ms", ".search-ms", ".diagcab",
    ];

    private static readonly string[] SystemVerbs =
    [
        "open", "opennew", "opennewprocess", "opennewtab", "opennewwindow", "runas", "runasuser", "edit",
        "print", "printto", "explore", "find", "cmd", "Powershell", "pintohome", "properties", "play",
        "preview", "openas", "Open with", "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork",
    ];

    public static IEnumerable<RegistryRule> BuildRules()
    {
        const RegistryRoot M = RegistryRoot.Machine;
        const RegistryRoot U = RegistryRoot.User;
        const string MachineCv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

        yield return Tree(M, "", "HKLM system hives");
        yield return Tree(M, "SYSTEM", "system configuration hive");
        yield return Tree(M, @"SYSTEM\CurrentControlSet", "active control set");
        yield return Tree(M, "SAM", "security account database");
        yield return Tree(M, "SECURITY", "local security authority secrets");
        yield return Tree(M, "BCD00000000", "boot configuration data");
        yield return Tree(M, "HARDWARE", "hardware description");
        yield return Self(M, "SOFTWARE", "machine software root");
        yield return Tree(M, @"SOFTWARE\Microsoft", "Windows and Microsoft components");
        yield return Tree(M, @"SOFTWARE\Policies", "group policy");
        yield return Self(M, @"SOFTWARE\Clients", "default client registrations");
        yield return Self(M, @"SOFTWARE\Clients\{any}", "default client category");
        yield return Self(M, @"SOFTWARE\RegisteredApplications", "default apps registrations");
        yield return Self(M, @"SOFTWARE\ODBC", "ODBC root");
        yield return Self(M, @"SOFTWARE\ODBC\ODBC.INI", "ODBC data sources");
        yield return Self(M, @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources", "ODBC data source list");
        yield return Self(M, @"SOFTWARE\ODBC\ODBCINST.INI", "ODBC drivers");
        yield return Self(M, @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers", "ODBC driver list");
        yield return Self(M, @"SYSTEM\CurrentControlSet\Services", "service registrations");

        foreach (RegistryRule rule in MicrosoftExceptions(M, @"SOFTWARE\Microsoft"))
        {
            yield return rule;
        }

        yield return Self(M, MachineCv + @"\SharedDLLs", "shared DLL reference counts");
        yield return Self(M, MachineCv + @"\Installer\Folders", "Windows Installer folder references");
        yield return Self(M, MachineCv + @"\Installer\UserData", "Windows Installer registrations");
        yield return Self(M, MachineCv + @"\Installer\UserData\{any}", "Windows Installer registrations per account");
        yield return Self(M, MachineCv + @"\Installer\UserData\{any}\Products", "Windows Installer products");
        yield return Self(M, MachineCv + @"\Installer\UserData\{any}\Components", "Windows Installer components");
        yield return Self(M, MachineCv + @"\Installer\UserData\{any}\Patches", "Windows Installer patches");
        yield return Self(M, MachineCv + @"\Explorer\ShellIconOverlayIdentifiers", "icon overlay handlers");
        yield return Self(M, MachineCv + @"\Explorer\Browser Helper Objects", "browser helper objects");
        yield return Self(M, @"SOFTWARE\Microsoft\Tracing", "RAS tracing registrations");
        yield return Self(M, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", "per-application dump settings");

        foreach (RegistryRule rule in Classes(M, @"SOFTWARE\Classes"))
        {
            yield return rule;
        }

        yield return Tree(U, "", "user hive settings");
        yield return Self(U, "Software", "user software root");
        yield return Tree(U, @"Software\Microsoft", "Windows and Microsoft settings");
        yield return Tree(U, @"Software\Policies", "group policy");
        yield return Self(U, @"Software\WOW6432Node", "user 32-bit software root");
        yield return Tree(U, @"Software\WOW6432Node\Microsoft", "Windows and Microsoft settings");
        yield return Tree(U, @"Software\WOW6432Node\Policies", "group policy");
        yield return Self(U, "Environment", "user environment variables");
        yield return Self(U, @"AppEvents\Schemes\Apps", "application sound events");
        yield return Tree(U, @"AppEvents\Schemes\Apps\.Default", "Windows sound events");
        yield return Tree(U, @"AppEvents\Schemes\Apps\Explorer", "Windows sound events");

        foreach (RegistryRule rule in MicrosoftExceptions(U, @"Software\Microsoft"))
        {
            yield return rule;
        }

        yield return Self(U, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", "per-user file type choices");
        yield return Self(U, @"Software\Microsoft\Windows\CurrentVersion\Start\TileProperties", "Start menu state per shortcut");

        foreach (RegistryRule rule in Classes(U, @"Software\Classes").Concat(Classes(U, @"Software\Classes\WOW6432Node")))
        {
            yield return rule;
        }
    }

    public static IEnumerable<ProtectedRegistryValue> BuildProtectedValues()
    {
        foreach (string name in new[] { "TEMP", "TMP", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            yield return new ProtectedRegistryValue(RegistryRoot.User, "Environment", name, "Windows environment variable");
        }

        yield return new ProtectedRegistryValue(RegistryRoot.User, "Environment", "Path", "user search path", ContentRevertible: true);
        yield return new ProtectedRegistryValue(
            RegistryRoot.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path", "machine search path", ContentRevertible: true);

        yield return new ProtectedRegistryValue(
            RegistryRoot.Machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", "Windows Security tray");
    }

    private static IEnumerable<RegistryRule> MicrosoftExceptions(RegistryRoot root, string microsoft)
    {
        string cv = microsoft + @"\Windows\CurrentVersion";
        string nt = microsoft + @"\Windows NT\CurrentVersion";

        yield return Self(root, cv + @"\Uninstall", "programs list");
        yield return Self(root, cv + @"\App Paths", "application paths");
        yield return Self(root, cv + @"\Run", "autostart entries");
        yield return Self(root, cv + @"\RunOnce", "autostart entries");
        yield return Self(root, cv + @"\Shell Extensions\Approved", "approved shell extensions");
        yield return Self(root, cv + @"\Shell Extensions\Blocked", "blocked shell extensions");
        yield return Self(root, cv + @"\Explorer\StartupApproved\Run", "autostart state");
        yield return Self(root, cv + @"\Explorer\StartupApproved\Run32", "autostart state");
        yield return Self(root, cv + @"\Explorer\StartupApproved\StartupFolder", "autostart state");
        yield return Self(root, nt + @"\AppCompatFlags\Layers", "compatibility settings per program");
        yield return Self(root, nt + @"\AppCompatFlags\Compatibility Assistant\Store", "compatibility assistant records");
        yield return Self(root, microsoft + @"\Internet Explorer\Low Rights\ElevationPolicy", "protected mode policy per program");
        yield return Self(root, microsoft + @"\Internet Explorer\Low Rights\DragDrop", "protected mode policy per program");
    }

    private static IEnumerable<RegistryRule> Classes(RegistryRoot root, string classes)
    {
        yield return Self(root, classes, "classes root");

        foreach (string name in new[] { "CLSID", "Interface", "TypeLib", "AppID", "Component Categories", "Applications", "Installer" })
        {
            yield return Self(root, $@"{classes}\{name}", "COM and shell registration root");
        }

        foreach (string name in new[] { "Products", "Features", "Components", "UpgradeCodes", "Patches", "Dependencies" })
        {
            yield return Self(root, $@"{classes}\Installer\{name}", "Windows Installer advertisement root");
        }

        yield return Self(root, classes + @"\MIME", "MIME root");
        yield return Self(root, classes + @"\MIME\Database", "MIME root");
        yield return Self(root, classes + @"\MIME\Database\Content Type", "MIME content types");
        yield return Self(root, classes + @"\SystemFileAssociations", "shared file type verbs");
        yield return Self(root, classes + @"\SystemFileAssociations\{any}", "shared file type verbs");
        yield return Self(root, classes + @"\SystemFileAssociations\{any}\shell", "shared file type verbs");
        yield return Self(root, classes + @"\SystemFileAssociations\{any}\shellex", "shared file type handlers");
        yield return Self(root, classes + @"\SystemFileAssociations\{any}\shellex\{any}", "shared file type handlers");

        foreach (string name in new[] { "Local Settings", "PackagedCom", "ActivatableClasses", "Extensions", "{prefix:AppX}" })
        {
            yield return Tree(root, $@"{classes}\{name}", "packaged app and shell state");
        }

        yield return Self(root, classes + @"\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "display names cached per program");

        foreach (string name in SystemClasses)
        {
            foreach (RegistryRule rule in SystemClass(root, $@"{classes}\{name}", withVerbs: true))
            {
                yield return rule;
            }
        }

        foreach (string name in SystemExtensions)
        {
            foreach (RegistryRule rule in SystemClass(root, $@"{classes}\{name}", withVerbs: false))
            {
                yield return rule;
            }
        }
    }

    private static IEnumerable<RegistryRule> SystemClass(RegistryRoot root, string key, bool withVerbs)
    {
        yield return Self(root, key, "Windows shell class");
        yield return Tree(root, key + @"\DefaultIcon", "Windows shell class icon");
        yield return Self(root, key + @"\OpenWithProgids", "open-with list");
        yield return Self(root, key + @"\OpenWithList", "open-with list");
        yield return Self(root, key + @"\Background", "Windows shell class");

        foreach (string branch in new[] { key, key + @"\Background" })
        {
            yield return Self(root, branch + @"\shell", "shell verbs");
            yield return Self(root, branch + @"\shellex", "shell extension handlers");
            yield return Self(root, branch + @"\shellex\{any}", "shell extension handler category");

            if (withVerbs)
            {
                foreach (string verb in SystemVerbs)
                {
                    yield return Tree(root, $@"{branch}\shell\{verb}", "Windows verb");
                }
            }
        }
    }

    private static RegistryRule Tree(RegistryRoot root, string path, string reason) =>
        new(root, Split(path), ProtectionKind.Tree, reason);

    private static RegistryRule Self(RegistryRoot root, string path, string reason) =>
        new(root, Split(path), ProtectionKind.Self, reason);

    private static string[] Split(string path) => path.Length == 0 ? [] : path.Split('\\');
}
