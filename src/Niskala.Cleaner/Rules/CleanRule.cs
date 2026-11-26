namespace Niskala.Cleaner.Rules;

public enum RuleAction
{
    Delete,
    Compact,
}

public enum RuleGroup
{
    System,
    Browser,
}

public sealed record CleanRule
{
    public required string Id { get; init; }

    public required CleanCategory Category { get; init; }

    public required LocalizedText Name { get; init; }

    public LocalizedText? Description { get; init; }

    public bool Quick { get; init; }

    public RemovalMode Removal { get; init; }

    public bool RequiresElevation { get; init; }

    public TimeSpan MinimumAge { get; init; }

    public RuleAction Action { get; init; }

    public IReadOnlyList<TargetDefinition> Targets { get; init; } = [];

    public BrowserDefinition? Browser { get; init; }

    public string? ProfileMarker { get; init; }

    public ProfileArea Area { get; init; }

    public IReadOnlyList<string> Databases { get; init; } = [];

    public RuleGroup Group => Browser is null ? RuleGroup.System : RuleGroup.Browser;

    public string DisplayName(bool indonesian) => Browser is null ? Name.For(indonesian) : $"{Browser.Name}: {Name.For(indonesian)}";
}
