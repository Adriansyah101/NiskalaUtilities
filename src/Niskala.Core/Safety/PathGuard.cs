using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Diagnostics;

namespace Niskala.Core.Safety;

public sealed class PathGuard(ProtectedLocations locations, IPathResolver resolver, ILogger<PathGuard>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public static PathGuard ForCurrentMachine(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new PathGuard(
            ProtectedLocations.ForCurrentMachine(loggerFactory.CreateLogger<ProtectedLocations>()),
            Win32PathResolver.Instance,
            loggerFactory.CreateLogger<PathGuard>());
    }

    public PathDecision Check(string? path)
    {
        string input = path ?? string.Empty;

        PathVerdict syntax = PathSyntax.TryNormalize(input, out string normalized);
        if (syntax != PathVerdict.Allowed)
        {
            return Deny(new PathDecision { Input = input, Verdict = syntax });
        }

        (PathVerdict lexical, ProtectedLocation? lexicalRule) = locations.Evaluate(normalized);
        if (lexical != PathVerdict.Allowed)
        {
            return Deny(new PathDecision { Input = input, Verdict = lexical, NormalizedPath = normalized, MatchedRule = lexicalRule });
        }

        PathResolution resolution = resolver.Resolve(normalized);
        if (resolution.Status != ResolutionStatus.Resolved)
        {
            return Deny(new PathDecision
            {
                Input = input,
                Verdict = resolution.Status switch
                {
                    ResolutionStatus.MissingDrive => PathVerdict.MissingDrive,
                    ResolutionStatus.NetworkDrive => PathVerdict.NetworkDrive,
                    ResolutionStatus.UnresolvedShortName => PathVerdict.UnresolvedShortName,
                    _ => PathVerdict.Unresolvable,
                },
                NormalizedPath = normalized,
                Win32Error = resolution.Win32Error,
            });
        }

        (PathVerdict physical, ProtectedLocation? physicalRule) = locations.Evaluate(resolution.CanonicalPath!);
        var decision = new PathDecision
        {
            Input = input,
            Verdict = physical,
            NormalizedPath = normalized,
            CanonicalPath = resolution.CanonicalPath,
            Exists = resolution.Exists,
            IsReparsePoint = resolution.IsReparsePoint,
            IsNameSurrogate = resolution.IsNameSurrogate,
            MatchedRule = physicalRule,
        };

        return decision.IsAllowed ? decision : Deny(decision);
    }

    private PathDecision Deny(PathDecision decision)
    {
        CoreLog.PathDenied(_logger, decision.Verdict, decision.Input, decision.CanonicalPath, decision.MatchedRule?.Reason);
        return decision;
    }
}
