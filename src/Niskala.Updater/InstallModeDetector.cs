using Microsoft.Win32;

namespace Niskala.Updater;

public enum InstallMode
{
    Portable,
    Installed,
}

public sealed record InstallInfo(InstallMode Mode, string Root)
{
    public bool IsInstalled => Mode == InstallMode.Installed;
}

public interface IInstallLocationSource
{
    IReadOnlyList<string> InstallLocations();
}

public sealed class RegistryInstallLocations : IInstallLocationSource
{
    public const string AppId = "{3EC397E4-E199-4B2F-B436-8F7D0FC248B9}";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
    private const string InstallLocationValue = "InstallLocation";

    private RegistryInstallLocations()
    {
    }

    public static RegistryInstallLocations Instance { get; } = new();

    public IReadOnlyList<string> InstallLocations()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? entry = machine.OpenSubKey(UninstallKey);
        return entry?.GetValue(InstallLocationValue) is string location && location.Length > 0 ? [location] : [];
    }
}

public sealed class InstallModeDetector(IInstallLocationSource registry)
{
    public const string UninstallerPattern = "unins???.exe";

    public const string LibraryFolder = "lib";

    private const int ParentLevels = 2;

    public InstallInfo Detect(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        string here = Normalize(baseDirectory);
        foreach (string location in registry.InstallLocations())
        {
            string root = Normalize(location);
            if (IsSameOrBelow(here, root))
            {
                return new InstallInfo(InstallMode.Installed, root);
            }
        }

        string? folder = here;
        for (int level = 0; level <= ParentLevels && folder is not null; level++)
        {
            if (HasUninstaller(folder))
            {
                return new InstallInfo(InstallMode.Installed, folder);
            }

            folder = Path.GetDirectoryName(folder);
        }

        bool inLibrary = Path.GetFileName(here).Equals(LibraryFolder, StringComparison.OrdinalIgnoreCase);
        return new InstallInfo(InstallMode.Portable, inLibrary ? Path.GetDirectoryName(here) ?? here : here);
    }

    private static bool HasUninstaller(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, UninstallerPattern)
                .Any(file => File.Exists(Path.ChangeExtension(file, ".dat")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSameOrBelow(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == Path.DirectorySeparatorChar);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
}
