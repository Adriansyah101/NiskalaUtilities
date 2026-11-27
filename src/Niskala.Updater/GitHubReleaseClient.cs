using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Niskala.Updater;

public static class ReleaseNames
{
    public const string Product = "NiskalaUtilities";
    public const string Checksums = "SHA256SUMS.txt";

    public static string Runtime { get; } = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    public static string Installer(ReleaseVersion version, string runtime) => $"{Product}-Setup-v{version}-{runtime}.exe";

    public static string Portable(ReleaseVersion version, string runtime) => $"{Product}-v{version}-{runtime}.zip";

    public static string Cli(ReleaseVersion version, string runtime) => $"{Product}-CLI-v{version}-{runtime}.zip";
}

public static class UpdateHttp
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static HttpClient Create(ReleaseVersion version) => Create(version, new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = ConnectTimeout,
    });

    public static HttpClient Create(ReleaseVersion version, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(version);
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ReleaseNames.Product, version.ToString()));
        return client;
    }
}

public sealed class GitHubReleaseClient(HttpClient http, ReleaseVersion current, string runtime)
{
    private const string MediaType = "application/vnd.github+json";
    private const string ApiVersionHeader = "X-GitHub-Api-Version";
    private const string ApiVersion = "2022-11-28";
    private const string DownloadHost = "github.com";

    public static Uri LatestRelease { get; } = new("https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/latest");

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.ParseAdd(MediaType);
        request.Headers.Add(ApiVersionHeader, ApiVersion);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Evaluate(null);
        }

        response.EnsureSuccessStatusCode();
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(await JsonSerializer.DeserializeAsync(body, UpdateJsonContext.Default.GitHubRelease, cancellationToken).ConfigureAwait(false));
    }

    internal UpdateCheck Evaluate(GitHubRelease? release)
    {
        if (release is null || release.Draft || release.Prerelease
            || !ReleaseVersion.TryParse(release.TagName, out ReleaseVersion? latest) || latest.IsPrerelease)
        {
            return new UpdateCheck(UpdateStatus.NoRelease, current, null, null, null, null, null, ReleaseAssets.None);
        }

        List<GitHubAsset> assets = release.Assets ?? [];
        var found = new ReleaseAssets(
            Find(assets, ReleaseNames.Installer(latest, runtime)),
            Find(assets, ReleaseNames.Portable(latest, runtime)),
            Find(assets, ReleaseNames.Cli(latest, runtime)),
            Find(assets, ReleaseNames.Checksums));

        return new UpdateCheck(
            latest > current ? UpdateStatus.Available : UpdateStatus.UpToDate,
            current,
            latest,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name.Trim(),
            release.Body?.ReplaceLineEndings().Trim(),
            Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? page) && page.Scheme == Uri.UriSchemeHttps ? page : null,
            release.PublishedAt,
            found);
    }

    private static ReleaseAsset? Find(List<GitHubAsset> assets, string name)
    {
        foreach (GitHubAsset asset in assets)
        {
            if (string.Equals(asset.Name, name, StringComparison.Ordinal)
                && Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? url)
                && url.Scheme == Uri.UriSchemeHttps
                && string.Equals(url.Host, DownloadHost, StringComparison.OrdinalIgnoreCase))
            {
                return new ReleaseAsset(name, url, asset.Size);
            }
        }

        return null;
    }
}
