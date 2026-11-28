using System.Text;

namespace Niskala.Updater.Tests;

public sealed class ReleaseDownloaderTests
{
    private const string Name = "NiskalaUtilities-v0.2.0-win-x64.zip";

    private static readonly byte[] Package = Encoding.ASCII.GetBytes("PK-niskala-package");

    private static ReleaseAsset Asset(long size) => new(Name, new Uri(Releases.Base + Name), size);

    private static ReleaseAsset Sums => new(ReleaseNames.Checksums, new Uri(Releases.Base + ReleaseNames.Checksums), 0);

    private static FakeHandler Serve(byte[] package, string sums) =>
        new FakeHandler().Route(Releases.Base + Name, package).Route(Releases.Base + ReleaseNames.Checksums, sums);

    private static async Task<VerifiedDownload> DownloadAsync(FakeHandler handler, string folder, ReleaseAsset? asset = null, IProgress<DownloadProgress>? progress = null)
    {
        using HttpClient http = UpdateHttp.Create(Releases.V011, handler);
        return await new ReleaseDownloader(http).DownloadAsync(asset ?? Asset(Package.Length), Sums, folder, progress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_matching_package_is_kept_and_its_hash_returned()
    {
        string folder = TestRoots.NewFolder();
        var progress = new Recorder<DownloadProgress>();
        string sums = $"{new string('0', 64)}  other.zip\n{Releases.Hash(Package).ToUpperInvariant()}  {Name}\n";

        VerifiedDownload download = await DownloadAsync(Serve(Package, sums), folder, progress: progress);

        Assert.Equal(Path.Combine(folder, Name), download.Path);
        Assert.Equal(Package, await File.ReadAllBytesAsync(download.Path, TestContext.Current.CancellationToken));
        Assert.Equal(Releases.Hash(Package), download.Sha256);
        Assert.Equal(download.Sha256, ReleaseDownloader.Sha256Of(download.Path));
        Assert.Equal(new DownloadProgress(Name, 0, Package.Length), progress.Values[0]);
        Assert.Equal(new DownloadProgress(Name, Package.Length, Package.Length), progress.Values[^1]);
        Assert.Equal(100, progress.Values[^1].Percent);
    }

    [Fact]
    public async Task A_tampered_package_is_deleted_and_refused()
    {
        string folder = TestRoots.NewFolder();
        byte[] tampered = Encoding.ASCII.GetBytes("PK-niskala-packagf");

        UpdateIntegrityException error = await Assert.ThrowsAsync<UpdateIntegrityException>(
            () => DownloadAsync(Serve(tampered, $"{Releases.Hash(Package)}  {Name}\n"), folder));

        Assert.Contains(Releases.Hash(tampered), error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task A_package_missing_from_the_checksums_is_never_downloaded()
    {
        string folder = TestRoots.NewFolder();
        FakeHandler handler = Serve(Package, $"{Releases.Hash(Package)}  NiskalaUtilities-Setup-v0.2.0-win-arm64.exe\n");

        await Assert.ThrowsAsync<UpdateIntegrityException>(() => DownloadAsync(handler, folder));

        Assert.Single(handler.Seen);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task A_short_package_is_refused_even_before_hashing()
    {
        string folder = TestRoots.NewFolder();
        await Assert.ThrowsAsync<UpdateIntegrityException>(
            () => DownloadAsync(Serve(Package, $"{Releases.Hash(Package)}  {Name}\n"), folder, Asset(Package.Length + 1)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task Malformed_or_missing_checksums_stop_the_download()
    {
        string folder = TestRoots.NewFolder();
        await Assert.ThrowsAsync<FormatException>(() => DownloadAsync(Serve(Package, "not a checksum file"), folder));
        await Assert.ThrowsAsync<HttpRequestException>(() => DownloadAsync(new FakeHandler().Route(Releases.Base + Name, Package), folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task An_oversized_checksum_file_is_refused()
    {
        string folder = TestRoots.NewFolder();
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => DownloadAsync(Serve(Package, new string(' ', 70 * 1024)), folder));
    }

    [Fact]
    public async Task Asset_names_with_folders_are_refused()
    {
        string folder = TestRoots.NewFolder();
        var escape = new ReleaseAsset(@"..\evil.exe", new Uri(Releases.Base + "evil.exe"), 4);
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => DownloadAsync(Serve(Package, string.Empty), folder, escape));
        Assert.False(File.Exists(Path.Combine(TestRoots.Folder, "evil.exe")));
    }
}
