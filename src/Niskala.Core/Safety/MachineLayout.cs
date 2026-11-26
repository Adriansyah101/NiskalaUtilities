using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Diagnostics;

namespace Niskala.Core.Safety;

public sealed record MachineLayout
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string LocalAccountSidPrefix = "S-1-5-21-";
    private const string EntraIdSidPrefix = "S-1-12-1-";

    public required string WindowsDirectory { get; init; }

    public required IReadOnlyList<string> ProgramFilesDirectories { get; init; }

    public required IReadOnlyList<string> CommonProgramFilesDirectories { get; init; }

    public required string ProgramDataDirectory { get; init; }

    public string? ProfilesDirectory { get; init; }

    public string? PublicProfile { get; init; }

    public string? DefaultProfile { get; init; }

    public required IReadOnlyList<string> UserProfiles { get; init; }

    public IReadOnlyList<string> CurrentUserKnownFolders { get; init; } = [];

    public required IReadOnlyList<string> DriveRoots { get; init; }

    public IReadOnlyList<string> ApplicationDirectories { get; init; } = [];

    public static MachineLayout Detect(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var profiles = new List<string>();
        string? profilesDirectory = null, publicProfile = null, defaultProfile = null;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? list = baseKey.OpenSubKey(ProfileListKey);
            if (list is not null)
            {
                profilesDirectory = ReadExpanded(list, "ProfilesDirectory");
                publicProfile = ReadExpanded(list, "Public");
                defaultProfile = ReadExpanded(list, "Default");

                foreach (string sid in list.GetSubKeyNames())
                {
                    if (!sid.StartsWith(LocalAccountSidPrefix, StringComparison.OrdinalIgnoreCase)
                        && !sid.StartsWith(EntraIdSidPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using RegistryKey? entry = list.OpenSubKey(sid);
                    if (entry is not null && ReadExpanded(entry, "ProfileImagePath") is { } path)
                    {
                        profiles.Add(path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            CoreLog.ProfileListReadFailed(logger, ex);
        }

        profiles.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new MachineLayout
        {
            WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ProgramFilesDirectories = Distinct(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")),
            CommonProgramFilesDirectories = Distinct(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetEnvironmentVariable("CommonProgramW6432")),
            ProgramDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProfilesDirectory = profilesDirectory,
            PublicProfile = publicProfile,
            DefaultProfile = defaultProfile,
            UserProfiles = Distinct([.. profiles]),
            CurrentUserKnownFolders = Distinct(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                localAppData,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                Path.GetTempPath()),
            DriveRoots = Distinct([.. DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName)]),
            ApplicationDirectories = Distinct(
                AppContext.BaseDirectory,
                ReleaseRoot(AppContext.BaseDirectory),
                localAppData.Length > 0 ? Path.Combine(localAppData, "Niskala") : null),
        };
    }

    private static string? ReleaseRoot(string baseDirectory)
    {
        string folder = Path.TrimEndingDirectorySeparator(baseDirectory);
        return Path.GetFileName(folder).Equals("lib", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) : null;
    }

    private static string? ReadExpanded(RegistryKey key, string name) =>
        key.GetValue(name) is string { Length: > 0 } value ? Environment.ExpandEnvironmentVariables(value) : null;

    private static string[] Distinct(params string?[] values) =>
        [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => TrimSeparator(v!)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }
}
