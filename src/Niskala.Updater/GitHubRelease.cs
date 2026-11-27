using System.Text.Json.Serialization;

namespace Niskala.Updater;

internal sealed class GitHubRelease
{
    public string? TagName { get; set; }

    public string? Name { get; set; }

    public string? Body { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    public string? Name { get; set; }

    public string? BrowserDownloadUrl { get; set; }

    public long Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
