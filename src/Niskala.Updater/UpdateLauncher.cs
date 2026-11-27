using System.Diagnostics;
using Niskala.Interop.Processes;
using Niskala.Interop.Shell;

namespace Niskala.Updater;

public enum UpdateAction
{
    InstallerStarted,
    PackageSaved,
}

public sealed record UpdateLaunch(UpdateAction Action, string Path);

public interface IUpdateShell
{
    string DownloadsFolder { get; }

    void Start(string program, string arguments);

    void Reveal(string path);
}

public sealed class SystemUpdateShell : IUpdateShell
{
    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private SystemUpdateShell()
    {
    }

    public static SystemUpdateShell Instance { get; } = new();

    public string DownloadsFolder =>
        KnownFolders.Find(KnownFolders.Downloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public void Start(string program, string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
    }

    public void Reveal(string path) => UnelevatedShell.TryStart(Explorer, $"\"{Explorer}\" /select,\"{path}\"");
}

public sealed class UpdateLauncher(IUpdateShell shell)
{
    public const string RestartArguments = "/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";
    public const string NoRestartArguments = "/VERYSILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS";

    public UpdateLaunch Launch(VerifiedDownload download, InstallMode mode, bool restart)
    {
        ArgumentNullException.ThrowIfNull(download);
        string actual = ReleaseDownloader.Sha256Of(download.Path);
        if (!string.Equals(actual, download.Sha256, StringComparison.Ordinal))
        {
            throw new UpdateIntegrityException($"{download.Asset.Name} changed after it was verified (SHA-256 {actual}); it was not opened.");
        }

        if (mode == InstallMode.Installed)
        {
            shell.Start(download.Path, restart ? RestartArguments : NoRestartArguments);
            return new UpdateLaunch(UpdateAction.InstallerStarted, download.Path);
        }

        string saved = Save(download);
        shell.Reveal(saved);
        return new UpdateLaunch(UpdateAction.PackageSaved, saved);
    }

    private string Save(VerifiedDownload download)
    {
        string folder = shell.DownloadsFolder;
        Directory.CreateDirectory(folder);
        string name = Path.GetFileNameWithoutExtension(download.Asset.Name);
        string extension = Path.GetExtension(download.Asset.Name);
        string target = Path.Combine(folder, download.Asset.Name);
        for (int copy = 2; File.Exists(target); copy++)
        {
            if (string.Equals(ReleaseDownloader.Sha256Of(target), download.Sha256, StringComparison.Ordinal))
            {
                Discard(download.Path);
                return target;
            }

            target = Path.Combine(folder, $"{name} ({copy}){extension}");
        }

        File.Move(download.Path, target);
        Discard(download.Path);
        return target;
    }

    private static void Discard(string path)
    {
        File.Delete(path);
        if (Path.GetDirectoryName(path) is { } folder
            && Path.GetFileName(folder).StartsWith(ReleaseDownloader.FolderPrefix, StringComparison.Ordinal)
            && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }
}
