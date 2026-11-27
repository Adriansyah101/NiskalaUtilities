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
    public void An_installed_copy_runs_the_installer_silently(bool restart, string arguments)
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        VerifiedDownload download = Stage("NiskalaUtilities-Setup-v0.2.0-win-x64.exe");

        UpdateLaunch launch = new UpdateLauncher(shell).Launch(download, InstallMode.Installed, restart);

        Assert.Equal(new UpdateLaunch(UpdateAction.InstallerStarted, download.Path), launch);
        Assert.Equal((download.Path, arguments), Assert.Single(shell.Started));
        Assert.Empty(shell.Revealed);
    }

    [Fact]
    public void A_package_changed_after_verification_is_not_run()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        VerifiedDownload download = Stage("NiskalaUtilities-Setup-v0.2.0-win-x64.exe");
        File.WriteAllText(download.Path, "swapped");

        Assert.Throws<UpdateIntegrityException>(() => new UpdateLauncher(shell).Launch(download, InstallMode.Installed, restart: true));
        Assert.Empty(shell.Started);
        Assert.Throws<UpdateIntegrityException>(() => new UpdateLauncher(shell).Launch(download, InstallMode.Portable, restart: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(shell.DownloadsFolder));
    }

    [Fact]
    public void A_portable_copy_saves_the_package_to_downloads_and_shows_it()
    {
        var shell = new FakeShell(Path.Combine(TestRoots.NewFolder(), "Downloads"));
        VerifiedDownload download = Stage("NiskalaUtilities-v0.2.0-win-x64.zip");

        UpdateLaunch launch = new UpdateLauncher(shell).Launch(download, InstallMode.Portable, restart: true);

        string saved = Path.Combine(shell.DownloadsFolder, "NiskalaUtilities-v0.2.0-win-x64.zip");
        Assert.Equal(new UpdateLaunch(UpdateAction.PackageSaved, saved), launch);
        Assert.Equal(Package, File.ReadAllBytes(saved));
        Assert.Equal(saved, Assert.Single(shell.Revealed));
        Assert.Empty(shell.Started);
        Assert.False(Directory.Exists(Path.GetDirectoryName(download.Path)));
    }

    [Fact]
    public void An_older_file_with_the_same_name_is_kept()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        string existing = Path.Combine(shell.DownloadsFolder, "NiskalaUtilities-v0.2.0-win-x64.zip");
        File.WriteAllText(existing, "something else");

        UpdateLaunch launch = new UpdateLauncher(shell).Launch(Stage("NiskalaUtilities-v0.2.0-win-x64.zip"), InstallMode.Portable, restart: false);

        Assert.Equal(Path.Combine(shell.DownloadsFolder, "NiskalaUtilities-v0.2.0-win-x64 (2).zip"), launch.Path);
        Assert.Equal("something else", File.ReadAllText(existing));
        Assert.Equal(Package, File.ReadAllBytes(launch.Path));
    }

    [Fact]
    public void The_same_package_already_in_downloads_is_reused()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        string existing = Path.Combine(shell.DownloadsFolder, "NiskalaUtilities-v0.2.0-win-x64.zip");
        File.WriteAllBytes(existing, Package);
        VerifiedDownload download = Stage("NiskalaUtilities-v0.2.0-win-x64.zip");

        UpdateLaunch launch = new UpdateLauncher(shell).Launch(download, InstallMode.Portable, restart: false);

        Assert.Equal(existing, launch.Path);
        Assert.Single(Directory.EnumerateFiles(shell.DownloadsFolder));
        Assert.False(File.Exists(download.Path));
    }
}
