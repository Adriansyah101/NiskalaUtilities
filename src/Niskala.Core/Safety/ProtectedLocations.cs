using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Diagnostics;

namespace Niskala.Core.Safety;

public sealed class ProtectedLocations
{
    private readonly ProtectedLocation[] _rules;

    private ProtectedLocations(ProtectedLocation[] rules) => _rules = rules;

    public IReadOnlyList<ProtectedLocation> Rules => _rules;

    public static ProtectedLocations Create(IEnumerable<ProtectedLocation> rules, IPathResolver resolver, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(resolver);
        logger ??= NullLogger.Instance;

        var result = new List<ProtectedLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProtectedLocation rule in rules)
        {
            PathVerdict verdict = PathSyntax.TryNormalize(rule.Path, out string normalized);
            if (verdict != PathVerdict.Allowed)
            {
                CoreLog.ProtectedRuleSkipped(logger, rule.Path, verdict);
                continue;
            }

            Add(rule with { Path = normalized });

            PathResolution resolution = resolver.Resolve(normalized);
            if (resolution.Status == ResolutionStatus.Resolved
                && !string.Equals(resolution.CanonicalPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Add(rule with { Path = resolution.CanonicalPath!, Reason = rule.Reason + " (resolved)" });
            }
        }

        return new ProtectedLocations([.. result]);

        void Add(ProtectedLocation rule)
        {
            if (seen.Add($"{rule.Kind}|{rule.Path}|{rule.ChildNamePrefix}"))
            {
                result.Add(rule);
            }
        }
    }

    public static ProtectedLocations FromLayout(MachineLayout layout, IPathResolver resolver, ILogger? logger = null) =>
        Create(DefaultProtectedLocations.Build(layout), resolver, logger);

    public static ProtectedLocations ForCurrentMachine(ILogger? logger = null) =>
        FromLayout(MachineLayout.Detect(logger), Win32PathResolver.Instance, logger);

    internal (PathVerdict Verdict, ProtectedLocation? Rule) Evaluate(string path)
    {
        if (PathSyntax.IsDriveRoot(path))
        {
            return (PathVerdict.DriveRoot, null);
        }

        ProtectedLocation? deepest = null;
        bool deepestIsSame = false;
        ProtectedLocation? contained = null;

        foreach (ProtectedLocation rule in _rules)
        {
            if (rule.ChildNamePrefix is { } prefix)
            {
                if (MatchesChildPrefix(path, rule.Path, prefix))
                {
                    return (PathVerdict.ProtectedTree, rule);
                }

                continue;
            }

            if (PathSyntax.IsSameOrUnder(path, rule.Path, out bool isSame))
            {
                if (deepest is null
                    || rule.Path.Length > deepest.Path.Length
                    || (rule.Path.Length == deepest.Path.Length && rule.Kind == ProtectionKind.Tree))
                {
                    deepest = rule;
                    deepestIsSame = isSame;
                }
            }
            else if (contained is null && PathSyntax.IsSameOrUnder(rule.Path, path, out _))
            {
                contained = rule;
            }
        }

        if (deepest?.Kind == ProtectionKind.Tree)
        {
            return (PathVerdict.ProtectedTree, deepest);
        }

        if (deepest is not null && deepestIsSame)
        {
            return (PathVerdict.ProtectedSelf, deepest);
        }

        return contained is null ? (PathVerdict.Allowed, null) : (PathVerdict.ContainsProtected, contained);
    }

    private static bool MatchesChildPrefix(string path, string parent, string prefix)
    {
        if (!PathSyntax.IsSameOrUnder(path, parent, out bool isSame) || isSame)
        {
            return false;
        }

        int start = parent.EndsWith('\\') ? parent.Length : parent.Length + 1;
        int end = path.IndexOf('\\', start);
        ReadOnlySpan<char> child = end < 0 ? path.AsSpan(start) : path.AsSpan(start, end - start);
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
