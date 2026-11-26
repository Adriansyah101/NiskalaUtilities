namespace Niskala.Core.Safety;

public sealed record PathDecision
{
    public required string Input { get; init; }

    public required PathVerdict Verdict { get; init; }

    public bool IsAllowed => Verdict == PathVerdict.Allowed;

    public string? NormalizedPath { get; init; }

    public string? CanonicalPath { get; init; }

    public bool Exists { get; init; }

    public bool IsReparsePoint { get; init; }

    public bool IsNameSurrogate { get; init; }

    public ProtectedLocation? MatchedRule { get; init; }

    public int Win32Error { get; init; }

    public IReadOnlyList<string> AllowedForms => !IsAllowed || NormalizedPath is null ? []
        : CanonicalPath is null || string.Equals(NormalizedPath, CanonicalPath, StringComparison.OrdinalIgnoreCase) ? [NormalizedPath]
        : [NormalizedPath, CanonicalPath];

    public override string ToString() => MatchedRule is null
        ? $"{Verdict}: {CanonicalPath ?? NormalizedPath ?? Input}"
        : $"{Verdict}: {CanonicalPath ?? NormalizedPath ?? Input} [{MatchedRule}]";
}
