using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niskala.Updater.Tests;

public sealed class UpdateServiceTests
{
    private const string Latest = "https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/latest";
    private const string Version = "99.0.0";
    private const string Installer = "NiskalaUtilities-Setup-v99.0.0-win-x64.exe";

    private static readonly byte[] Package = Encoding.ASCII.GetBytes("abcd");

    private static UpdateService Service(FakeHandler handler, FakeShell shell) => new(
        UpdateHttp.Create(UpdateService.CurrentVersion, handler),
        shell,
        NullLogger<UpdateService>.Instance);

    private static FakeHandler Release(byte[] package, string hash) => new FakeHandler()
        .Route(Latest, Releases.Json(Version))
        .Route(Releases.BaseOf(Version) + Installer, package)
        .Route(Releases.BaseOf(Version) + ReleaseNames.Checksums, $"{hash}  {Installer}\n");

    [Fact]
    public async Task A_verified_installer_is_started_and_nothing_else_is_fetched()
    {
        var shell = new FakeShell();
        FakeHandler handler = Release(Package, Releases.Hash(Package));
        using UpdateService service = Service(handler, shell);
        CancellationToken token = TestContext.Current.CancellationToken;

        UpdateCheck check = await service.CheckAsync(token);
        Assert.True(check.IsAvailable);
        Assert.Equal(Installer, UpdateService.PackageFor(check)?.Name);

        VerifiedDownload download = await service.DownloadAsync(check, null, token);
        string temporary = Path.GetDirectoryName(download.Path)!;
        Assert.StartsWith(ReleaseDownloader.FolderPrefix, Path.GetFileName(temporary), StringComparison.Ordinal);

        UpdateLaunch launch = service.Apply(download, restart: true);
        Assert.Equal(download.Path, launch.Path);
        Assert.Equal((download.Path, UpdateLauncher.RestartArguments), Assert.Single(shell.Started));
        Assert.DoesNotContain(handler.Seen, r => r.Url.AbsoluteUri.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Equal(3, handler.Seen.Count);
        UpdateService.Discard(download);
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public async Task A_rejected_download_removes_its_temporary_folder()
    {
        var shell = new FakeShell();
        using UpdateService service = Service(Release(Package, new string('a', 64)), shell);
        CancellationToken token = TestContext.Current.CancellationToken;
        UpdateCheck check = await service.CheckAsync(token);
        string[] before = TemporaryFolders();

        await Assert.ThrowsAsync<UpdateIntegrityException>(() => service.DownloadAsync(check, null, token));

        Assert.Equal(before, TemporaryFolders());
        Assert.Empty(shell.Started);
    }

    [Fact]
    public async Task A_dry_run_discard_removes_the_verified_package()
    {
        using UpdateService service = Service(Release(Package, Releases.Hash(Package)), new FakeShell());
        CancellationToken token = TestContext.Current.CancellationToken;

        VerifiedDownload download = await service.DownloadAsync(await service.CheckAsync(token), null, token);
        UpdateService.Discard(download);

        Assert.False(Directory.Exists(Path.GetDirectoryName(download.Path)));
    }

    [Fact]
    public async Task A_release_without_an_installer_downloads_nothing()
    {
        var handler = new FakeHandler().Route(Latest, """{ "tag_name": "v99.0.0", "assets": [] }""");
        using UpdateService service = Service(handler, new FakeShell());
        CancellationToken token = TestContext.Current.CancellationToken;

        UpdateCheck check = await service.CheckAsync(token);
        Assert.Null(UpdateService.PackageFor(check));
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => service.DownloadAsync(check, null, token));
        Assert.Single(handler.Seen);
    }

    private static string[] TemporaryFolders() =>
        [.. Directory.EnumerateDirectories(Path.GetTempPath(), ReleaseDownloader.FolderPrefix + "*").Order(StringComparer.OrdinalIgnoreCase)];
}
