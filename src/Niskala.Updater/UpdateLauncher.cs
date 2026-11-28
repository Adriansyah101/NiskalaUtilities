using System.Diagnostics;

namespace Niskala.Updater;

public sealed record UpdateLaunch(string Path, string Arguments);

public interface IUpdateShell
{
    void Start(string program, string arguments);
}

public sealed class SystemUpdateShell : IUpdateShell
{
    private SystemUpdateShell()
    {
    }

    public static SystemUpdateShell Instance { get; } = new();

    public void Start(string program, string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
    }
}

public sealed class UpdateLauncher(IUpdateShell shell)
{
    public const string RestartArguments = "/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";
    public const string NoRestartArguments = "/VERYSILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS";

    public UpdateLaunch Launch(VerifiedDownload download, bool restart)
    {
        ArgumentNullException.ThrowIfNull(download);
        string actual = ReleaseDownloader.Sha256Of(download.Path);
        if (!string.Equals(actual, download.Sha256, StringComparison.Ordinal))
        {
            throw new UpdateIntegrityException($"{download.Asset.Name} changed after it was verified (SHA-256 {actual}); it was not opened.");
        }

        string arguments = restart ? RestartArguments : NoRestartArguments;
        shell.Start(download.Path, arguments);
        return new UpdateLaunch(download.Path, arguments);
    }
}
