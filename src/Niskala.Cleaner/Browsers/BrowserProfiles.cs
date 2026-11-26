using Niskala.Cleaner.Rules;

namespace Niskala.Cleaner.Browsers;

public sealed record BrowserProfile(string BrowserId, string Name, string ProfileFolder, string CacheFolder, string UserDataFolder);

public enum ProfileLockState
{
    Free,
    LockedByFile,
    BrowserRunning,
}

public static class BrowserProfiles
{
    public const string ChromiumLockFile = "lockfile";
    public const string GeckoLockFile = "parent.lock";

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public static IReadOnlyList<BrowserProfile> Find(BrowserDefinition browser, string profileMarker, ICleanEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(environment);
        var profiles = new List<BrowserProfile>();
        for (int i = 0; i < browser.UserData.Count; i++)
        {
            if (Folder(browser.UserData[i], environment) is not { } userData || !Directory.Exists(userData) || FileFacts.IsLink(userData))
            {
                continue;
            }

            string? cacheBase = i < browser.CacheData.Count ? Folder(browser.CacheData[i], environment) : null;
            foreach (string folder in Candidates(userData))
            {
                if (!File.Exists(Path.Combine(folder, profileMarker)))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(userData, folder);
                string cache = cacheBase is null ? folder : relative == "." ? cacheBase : Path.Combine(cacheBase, relative);
                profiles.Add(new BrowserProfile(browser.Id, relative == "." ? Path.GetFileName(folder) : relative, folder, cache, userData));
            }
        }

        return profiles;
    }

    public static ProfileLockState LockState(BrowserDefinition browser, BrowserProfile profile, IReadOnlySet<string> runningProcesses)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(runningProcesses);
        string lockFile = browser.Engine == BrowserEngine.Chromium
            ? Path.Combine(profile.UserDataFolder, ChromiumLockFile)
            : Path.Combine(profile.ProfileFolder, GeckoLockFile);
        if (IsHeld(lockFile))
        {
            return ProfileLockState.LockedByFile;
        }

        return browser.Processes.Any(runningProcesses.Contains) ? ProfileLockState.BrowserRunning : ProfileLockState.Free;
    }

    public static bool IsHeld(string lockFile)
    {
        try
        {
            using FileStream stream = new(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            return true;
        }
    }

    private static IEnumerable<string> Candidates(string userData)
    {
        yield return userData;
        IEnumerable<string> children;
        try
        {
            children = [.. Directory.EnumerateDirectories(userData)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string child in children.Where(c => !FileFacts.IsLink(c)).Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return child;
        }
    }

    private static string? Folder(LocationDefinition location, ICleanEnvironment environment) =>
        environment.Resolve(location.Root) is { } root ? Path.Combine(root, location.Path) : null;
}
