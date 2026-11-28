using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Niskala.Updater.Tests;

internal sealed record SeenRequest(Uri Url, string UserAgent, string Accept, string? ApiVersion);

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _routes = new(StringComparer.Ordinal);

    public List<SeenRequest> Seen { get; } = [];

    public FakeHandler Route(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => Route(url, Encoding.UTF8.GetBytes(body), status);

    public FakeHandler Route(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = (status, body);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri url = request.RequestUri!;
        Seen.Add(new SeenRequest(
            url,
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString(),
            request.Headers.TryGetValues("X-GitHub-Api-Version", out IEnumerable<string>? values) ? values.Single() : null));

        var response = _routes.TryGetValue(url.AbsoluteUri, out var route)
            ? new HttpResponseMessage(route.Status) { Content = new ByteArrayContent(route.Body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        return Task.FromResult(response);
    }
}

internal sealed class FakeShell : IUpdateShell
{
    public List<(string Program, string Arguments)> Started { get; } = [];

    public void Start(string program, string arguments) => Started.Add((program, arguments));
}

internal sealed class Recorder<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}

internal static class Releases
{
    public const string Base = "https://github.com/Adriansyah101/NiskalaUtilities/releases/download/v0.2.0/";

    public static ReleaseVersion V011 { get; } = ReleaseVersion.Parse("0.1.1");

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string BaseOf(string version) => $"https://github.com/Adriansyah101/NiskalaUtilities/releases/download/v{version}/";

    public static string Json(string version = "0.2.0", string? tag = null, bool draft = false, bool prerelease = false, string host = "github.com")
    {
        (string Name, int Size)[] files =
        [
            ($"NiskalaUtilities-Setup-v{version}-win-x64.exe", 4),
            ($"NiskalaUtilities-v{version}-win-x64.zip", 5),
            ($"NiskalaUtilities-CLI-v{version}-win-x64.zip", 6),
            ($"NiskalaUtilities-Setup-v{version}-win-arm64.exe", 7),
            ("SHA256SUMS.txt", 300),
        ];

        string assets = string.Join(", ", files.Select(file =>
            $$"""{ "name": "{{file.Name}}", "size": {{file.Size}}, "browser_download_url": "https://{{host}}/Adriansyah101/NiskalaUtilities/releases/download/v{{version}}/{{file.Name}}" }"""));

        tag ??= "v" + version;
        return $$"""
            {
              "url": "https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/1",
              "html_url": "https://github.com/Adriansyah101/NiskalaUtilities/releases/tag/{{tag}}",
              "tag_name": "{{tag}}",
              "name": "Niskala Utilities {{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "published_at": "2026-12-01T08:30:00Z",
              "body": "Fixes.\r\n\r\n- Faster scans",
              "assets": [{{assets}}]
            }
            """;
    }
}
