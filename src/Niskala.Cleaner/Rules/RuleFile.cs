using System.Text.Json.Serialization;

namespace Niskala.Cleaner.Rules;

public enum CleanRoot
{
    UserTemp,
    WindowsTemp,
    Windows,
    LocalAppData,
    RoamingAppData,
    ProgramData,
}

public enum CleanCategory
{
    TemporaryFiles,
    ThumbnailCache,
    CrashDumps,
    ErrorReports,
    DeliveryOptimization,
    BrowserCache,
    BrowserHistory,
    BrowserCookies,
    BrowserSessions,
    BrowserForms,
    DatabaseCompaction,
}

public enum RemovalMode
{
    Permanent,
    RecycleBin,
}

public enum BrowserEngine
{
    Chromium,
    Gecko,
}

public enum ProfileArea
{
    Profile,
    Cache,
}

public sealed record LocalizedText
{
    public required string En { get; init; }

    public required string Id { get; init; }

    public string For(bool indonesian) => indonesian ? Id : En;
}

public sealed record TargetDefinition
{
    public CleanRoot? Root { get; init; }

    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<string> Files { get; init; } = ["*"];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool Recurse { get; init; }

    public bool KeepFolders { get; init; }
}

public sealed record RuleDefinition
{
    public required string Id { get; init; }

    public required CleanCategory Category { get; init; }

    public required LocalizedText Name { get; init; }

    public LocalizedText? Description { get; init; }

    public bool Quick { get; init; }

    public RemovalMode Removal { get; init; }

    public bool RequiresElevation { get; init; }

    public int MinimumAgeHours { get; init; }

    public required IReadOnlyList<TargetDefinition> Targets { get; init; }
}

public sealed record LocationDefinition
{
    public required CleanRoot Root { get; init; }

    public required string Path { get; init; }
}

public sealed record BrowserDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required BrowserEngine Engine { get; init; }

    public IReadOnlyList<string> Processes { get; init; } = [];

    public required IReadOnlyList<LocationDefinition> UserData { get; init; }

    public IReadOnlyList<LocationDefinition> CacheData { get; init; } = [];
}

public sealed record EngineItemDefinition
{
    public required string Id { get; init; }

    public required CleanCategory Category { get; init; }

    public required LocalizedText Name { get; init; }

    public LocalizedText? Description { get; init; }

    public bool Quick { get; init; }

    public RemovalMode Removal { get; init; }

    public ProfileArea Area { get; init; }

    public required IReadOnlyList<TargetDefinition> Targets { get; init; }
}

public sealed record EngineDefinition
{
    public required BrowserEngine Engine { get; init; }

    public required string ProfileMarker { get; init; }

    public IReadOnlyList<EngineItemDefinition> Items { get; init; } = [];

    public IReadOnlyList<string> Databases { get; init; } = [];
}

public sealed record RuleFile
{
    public required int SchemaVersion { get; init; }

    public IReadOnlyList<RuleDefinition> Rules { get; init; } = [];

    public IReadOnlyList<BrowserDefinition> Browsers { get; init; } = [];

    public IReadOnlyList<EngineDefinition> Engines { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(RuleFile))]
internal sealed partial class RuleJsonContext : JsonSerializerContext;
