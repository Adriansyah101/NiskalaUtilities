using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niskala.Updater.Tests;

public sealed class UpdateServiceTests
{
    private const string Latest = "https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/latest";
    private const string Version = "99.0.0";
    private const string Portable = "NiskalaUtilities-v99.0.0-win-x64.zip";

    private static readonly byte[] Package = Encoding.ASCII.GetBytes("abcde");

    private static UpdateService Service(FakeHandler handler, FakeShell shell, InstallMode mode) => new(
        UpdateProduct.Desktop,
        new InstallInfo(mode, TestRoots.Folder),
        UpdateHttp.Create(UpdateService.CurrentVersion, handler),
        shell,
        NullLogger<UpdateService>.Instance);

    private static FakeHandler Release(byte[] package, string hash) => new FakeHandler()
        .Route(Latest, Releases.Json(Version))
        .Route(Releases.BaseOf(Version) + Portable, package)
        .Route(Releases.BaseOf(Version) + ReleaseNames.Checksums, $"{hash}  {Portable}\n");

    [Fact]
    public async Task A_portable_update_lands_in_downloads_and_leaves_no_temporary_folder()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        using UpdateService service = Service(Release(Package, Releases.Hash(Package)), shell, InstallMode.Portable);
        CancellationToken token = TestContext.Current.CancellationToken;

        UpdateCheck check = await service.CheckAsync(token);
        Assert.True(check.IsAvailable);
        Assert.Equal(Portable, service.PackageFor(check)?.Name);

        VerifiedDownload download = await service.DownloadAsync(check, null, token);
        string temporary = Path.GetDirectoryName(download.Path)!;
        Assert.StartsWith(ReleaseDownloader.FolderPrefix, Path.GetFileName(temporary), StringComparison.Ordinal);

        UpdateLaunch launch = service.Apply(download, restart: true);
        Assert.Equal(UpdateAction.PackageSaved, launch.Action);
        Assert.Equal(Package, await File.ReadAllBytesAsync(launch.Path, token));
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public async Task A_rejected_download_removes_its_temporary_folder()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        using UpdateService service = Service(Release(Package, new string('a', 64)), shell, InstallMode.Portable);
        CancellationToken token = TestContext.Current.CancellationToken;
        UpdateCheck check = await service.CheckAsync(token);
        string[] before = TemporaryFolders();

        await Assert.ThrowsAsync<UpdateIntegrityException>(() => service.DownloadAsync(check, null, token));

        Assert.Equal(before, TemporaryFolders());
        Assert.Empty(shell.Started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(shell.DownloadsFolder));
    }

    [Fact]
    public async Task A_dry_run_discard_removes_the_verified_package()
    {
        var shell = new FakeShell(TestRoots.NewFolder());
        using UpdateService service = Service(Release(Package, Releases.Hash(Package)), shell, InstallMode.Portable);
        CancellationToken token = TestContext.Current.CancellationToken;

        VerifiedDownload download = await service.DownloadAsync(await service.CheckAsync(token), null, token);
        UpdateService.Discard(download);

        Assert.False(Directory.Exists(Path.GetDirectoryName(download.Path)));
    }

    [Fact]
    public async Task An_installed_copy_without_an_installer_asset_downloads_nothing()
    {
        var handler = new FakeHandler().Route(Latest, """{ "tag_name": "v99.0.0", "assets": [] }""");
        using UpdateService service = Service(handler, new FakeShell(TestRoots.NewFolder()), InstallMode.Installed);
        CancellationToken token = TestContext.Current.CancellationToken;

        UpdateCheck check = await service.CheckAsync(token);
        Assert.Null(service.PackageFor(check));
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => service.DownloadAsync(check, null, token));
        Assert.Single(handler.Seen);
    }

    private static string[] TemporaryFolders() =>
        [.. Directory.EnumerateDirectories(Path.GetTempPath(), ReleaseDownloader.FolderPrefix + "*").Order(StringComparer.OrdinalIgnoreCase)];
}
