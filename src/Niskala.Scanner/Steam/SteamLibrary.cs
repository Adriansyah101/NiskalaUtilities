using System.Globalization;
using Microsoft.Win32;
using Niskala.Core.Models;

namespace Niskala.Scanner.Steam;

public sealed record SteamManifest(int AppId, string Name, string InstallDir, long? SizeOnDisk);

public sealed record SteamGame(SteamManifest Manifest, string Library)
{
    public int AppId => Manifest.AppId;

    public string Apps => Path.Combine(Library, "steamapps");

    public string ManifestFile => Path.Combine(Apps, $"appmanifest_{Id}.acf");

    public string InstallFolder => Path.Combine(Apps, "common", Manifest.InstallDir);

    private string Id => AppId.ToString(CultureInfo.InvariantCulture);
}

public static class SteamLibrary
{
    public const string KeyPrefix = "Steam App ";

    public const string RunGameUrl = "steam://rungameid/";

    public static int? AppId(string? keyName) =>
        keyName is not null && keyName.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(keyName.AsSpan(KeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0
            ? id
            : null;

    public static int? AppIdFromUrl(string? url) =>
        url is not null && url.StartsWith(RunGameUrl, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(url.AsSpan(RunGameUrl.Length).TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0
            ? id
            : null;

    public static int? AppId(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.InstallerKind == InstallerKind.Steam ? AppId(app.KeyName) : null;
    }

    public static string? ClientFolder()
    {
        using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        string? installPath = machine?.GetValue("InstallPath") is string { Length: > 0 } path && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (user?.GetValue("SteamPath") is string { Length: > 0 } steamPath && Path.IsPathFullyQualified(steamPath))
        {
            string folder = Path.GetFullPath(steamPath.Replace('/', '\\'));
            return folder.Equals(installPath, StringComparison.OrdinalIgnoreCase) ? installPath : folder;
        }

        return installPath;
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string text)
    {
        if (VdfNode.Parse(text)["libraryfolders"] is not { } folders)
        {
            return [];
        }

        return
        [
            .. folders.Children
                .Where(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(c => c.Value.Value ?? c.Value.Text("path"))
                .OfType<string>()
                .Where(p => p.Length > 0),
        ];
    }

    public static SteamManifest? ParseManifest(string text)
    {
        if (VdfNode.Parse(text)["AppState"] is not { } state
            || !int.TryParse(state.Text("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out int appId)
            || state.Text("installdir") is not { } installDir
            || !IsFolderName(installDir))
        {
            return null;
        }

        long? size = long.TryParse(state.Text("SizeOnDisk"), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
        return new SteamManifest(appId, state.Text("name") ?? installDir, installDir, size);
    }

    public static SteamGame? Find(int appId, string clientFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientFolder);
        foreach (string library in Libraries(clientFolder))
        {
            var candidate = new SteamGame(new SteamManifest(appId, string.Empty, string.Empty, null), library);
            if (Read(candidate.ManifestFile, ParseManifest) is { } manifest && manifest.AppId == appId)
            {
                return new SteamGame(manifest, library);
            }
        }

        return null;
    }

    public static IReadOnlyList<string> Libraries(string clientFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientFolder);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientFolder));
        IReadOnlyList<string> listed = Read(Path.Combine(root, "steamapps", "libraryfolders.vdf"), ParseLibraryFolders) ?? [];
        return
        [
            .. listed
                .Where(Path.IsPathFullyQualified)
                .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Prepend(root)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static T? Read<T>(string file, Func<string, T?> parse)
        where T : class
    {
        try
        {
            return File.Exists(file) ? parse(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static bool IsFolderName(string name) =>
        name.Trim().Length > 0
        && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
