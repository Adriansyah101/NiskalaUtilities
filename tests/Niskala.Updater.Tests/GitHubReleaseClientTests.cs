using System.Net;
using System.Text.Json;

namespace Niskala.Updater.Tests;

public sealed class GitHubReleaseClientTests
{
    private const string Latest = "https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/latest";

    private static async Task<(UpdateCheck Check, FakeHandler Handler)> CheckAsync(string json, string current = "0.1.1", HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHandler().Route(Latest, json, status);
        using HttpClient http = UpdateHttp.Create(ReleaseVersion.Parse(current), handler);
        var client = new GitHubReleaseClient(http, ReleaseVersion.Parse(current), "win-x64");
        return (await client.CheckAsync(TestContext.Current.CancellationToken), handler);
    }

    [Fact]
    public async Task A_newer_release_is_available_with_its_four_assets()
    {
        (UpdateCheck check, _) = await CheckAsync(Releases.Json());

        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.True(check.IsAvailable);
        Assert.Equal("0.2.0", check.Latest?.ToString());
        Assert.Equal("Niskala Utilities v0.2.0", check.Title);
        Assert.Equal($"Fixes.{Environment.NewLine}{Environment.NewLine}- Faster scans", check.Notes);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero), check.Published);
        Assert.Equal("https://github.com/Adriansyah101/NiskalaUtilities/releases/tag/v0.2.0", check.Page?.AbsoluteUri);
        Assert.Equal("NiskalaUtilities-Setup-v0.2.0-win-x64.exe", check.Assets.Installer?.Name);
        Assert.Equal(4, check.Assets.Installer?.Size);
        Assert.Equal("NiskalaUtilities-v0.2.0-win-x64.zip", check.Assets.Portable?.Name);
        Assert.Equal("NiskalaUtilities-CLI-v0.2.0-win-x64.zip", check.Assets.Cli?.Name);
        Assert.Equal(Releases.Base + "SHA256SUMS.txt", check.Assets.Checksums?.DownloadUrl.AbsoluteUri);
    }

    [Fact]
    public async Task The_request_names_the_product_version_and_the_api()
    {
        (_, FakeHandler handler) = await CheckAsync(Releases.Json());

        SeenRequest request = Assert.Single(handler.Seen);
        Assert.Equal(Latest, request.Url.AbsoluteUri);
        Assert.Equal(GitHubReleaseClient.LatestRelease, request.Url);
        Assert.Equal("NiskalaUtilities/0.1.1", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);
    }

    [Theory]
    [InlineData("0.2.0", UpdateStatus.UpToDate)]
    [InlineData("0.3.0", UpdateStatus.UpToDate)]
    [InlineData("0.2.0-beta.1", UpdateStatus.Available)]
    [InlineData("0.1.9", UpdateStatus.Available)]
    public async Task The_running_version_decides_the_status(string current, UpdateStatus expected)
    {
        (UpdateCheck check, _) = await CheckAsync(Releases.Json(), current);
        Assert.Equal(expected, check.Status);
    }

    [Fact]
    public async Task Drafts_prereleases_and_prerelease_tags_are_skipped()
    {
        Assert.Equal(UpdateStatus.NoRelease, (await CheckAsync(Releases.Json(draft: true))).Check.Status);
        Assert.Equal(UpdateStatus.NoRelease, (await CheckAsync(Releases.Json(prerelease: true))).Check.Status);
        (UpdateCheck tagged, _) = await CheckAsync(Releases.Json(tag: "v0.2.0-rc.1"));
        Assert.Equal(UpdateStatus.NoRelease, tagged.Status);
        Assert.Null(tagged.Latest);
        Assert.Same(ReleaseAssets.None, tagged.Assets);
    }

    [Fact]
    public async Task No_published_release_is_not_an_error()
    {
        (UpdateCheck check, _) = await CheckAsync("{\"message\":\"Not Found\"}", status: HttpStatusCode.NotFound);
        Assert.Equal(UpdateStatus.NoRelease, check.Status);
        Assert.Equal(Releases.V011, check.Current);
    }

    [Fact]
    public async Task A_rate_limit_answer_is_reported()
    {
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => CheckAsync("{}", status: HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Assets_off_github_are_ignored()
    {
        (UpdateCheck check, _) = await CheckAsync(Releases.Json(host: "example.com"));
        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal(ReleaseAssets.None, check.Assets);
    }

    [Fact]
    public async Task Missing_fields_leave_the_release_without_assets()
    {
        (UpdateCheck check, _) = await CheckAsync("""{ "tag_name": "v0.2.0" }""");
        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal("v0.2.0", check.Title);
        Assert.Null(check.Notes);
        Assert.Null(check.Page);
        Assert.Equal(ReleaseAssets.None, check.Assets);
    }

    [Fact]
    public async Task Malformed_json_is_refused()
    {
        await Assert.ThrowsAsync<JsonException>(() => CheckAsync("""{ "tag_name": 7 }"""));
    }

    [Fact]
    public void Parsing_uses_source_generated_metadata_only()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<GitHubRelease>("{}"));
        GitHubRelease? release = JsonSerializer.Deserialize(Releases.Json(), UpdateJsonContext.Default.GitHubRelease);
        Assert.Equal(5, release?.Assets?.Count);
        Assert.Equal("v0.2.0", release?.TagName);
    }

    [Theory]
    [InlineData(InstallMode.Installed, UpdateProduct.Desktop, "NiskalaUtilities-Setup-v0.2.0-win-x64.exe")]
    [InlineData(InstallMode.Installed, UpdateProduct.CommandLine, "NiskalaUtilities-Setup-v0.2.0-win-x64.exe")]
    [InlineData(InstallMode.Portable, UpdateProduct.Desktop, "NiskalaUtilities-v0.2.0-win-x64.zip")]
    [InlineData(InstallMode.Portable, UpdateProduct.CommandLine, "NiskalaUtilities-CLI-v0.2.0-win-x64.zip")]
    public async Task Each_copy_gets_its_own_package(InstallMode mode, UpdateProduct product, string expected)
    {
        (UpdateCheck check, _) = await CheckAsync(Releases.Json());
        Assert.Equal(expected, check.Assets.For(mode, product)?.Name);
    }
}
