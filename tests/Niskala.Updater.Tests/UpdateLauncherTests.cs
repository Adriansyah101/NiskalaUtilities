using System.Text;

namespace Niskala.Updater.Tests;

public sealed class UpdateLauncherTests
{
    private static readonly byte[] Package = Encoding.ASCII.GetBytes("niskala-update-package");

    private static VerifiedDownload Stage(string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(TestRoots.NewFolder(), ReleaseDownloader.FolderPrefix + "test")).FullName;
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Package);
        return new VerifiedDownload(new ReleaseAsset(name, new Uri(Releases.Base + name), Package.Length), path, Releases.Hash(Package));
    }

    [Theory]
    [InlineData(true, "/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")]
    [InlineData(false, "/VERYSILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS")]
    public void The_installer_runs_silently(bool restart, string arguments)
    {
        var shell = new FakeShell();
        VerifiedDownload download = Stage("NiskalaUtilities-Setup-v0.2.0-win-x64.exe");

        UpdateLaunch launch = new UpdateLauncher(shell).Launch(download, restart);

        Assert.Equal(new UpdateLaunch(download.Path, arguments), launch);
        Assert.Equal((download.Path, arguments), Assert.Single(shell.Started));
    }

    [Fact]
    public void A_package_changed_after_verification_is_not_run()
    {
        var shell = new FakeShell();
        VerifiedDownload download = Stage("NiskalaUtilities-Setup-v0.2.0-win-x64.exe");
        File.WriteAllText(download.Path, "swapped");

        Assert.Throws<UpdateIntegrityException>(() => new UpdateLauncher(shell).Launch(download, restart: true));
        Assert.Empty(shell.Started);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Niskala Utilities\lib", @"C:\Program Files\Niskala Utilities")]
    [InlineData(@"C:\Program Files\Niskala Utilities\lib\", @"C:\Program Files\Niskala Utilities")]
    [InlineData(@"C:\Program Files\Niskala Utilities", @"C:\Program Files\Niskala Utilities")]
    public void The_install_root_is_the_folder_above_lib(string baseDirectory, string root) =>
        Assert.Equal(root, UpdateService.RootOf(baseDirectory));
}
