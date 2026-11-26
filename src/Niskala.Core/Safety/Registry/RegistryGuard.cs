using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Diagnostics;

namespace Niskala.Core.Safety.Registry;

public sealed record RegistryDecision
{
    public required string Input { get; init; }

    public string? ValueName { get; init; }

    public required RegistryVerdict Verdict { get; init; }

    public bool IsAllowed => Verdict == RegistryVerdict.Allowed;

    public RegistryPath? Path { get; init; }

    public string? MatchedRule { get; init; }

    public override string ToString() =>
        $"{Verdict}: {Path?.ToString() ?? Input}{(ValueName is null ? string.Empty : $" -> \"{ValueName}\"")}"
        + (MatchedRule is null ? string.Empty : $" [{MatchedRule}]");
}

public sealed class RegistryGuard
{
    private readonly RegistryRuleSet _rules;
    private readonly IServiceFacts _services;
    private readonly string _windowsDirectory;
    private readonly ILogger _logger;

    public RegistryGuard(RegistryRuleSet rules, IServiceFacts services, string windowsDirectory, ILogger<RegistryGuard>? logger = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (PathSyntax.TryNormalize(windowsDirectory, out string normalized) != PathVerdict.Allowed)
        {
            throw new ArgumentException("The Windows directory must be a valid drive path.", nameof(windowsDirectory));
        }

        _windowsDirectory = normalized;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public static RegistryGuard ForCurrentMachine(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new RegistryGuard(
            RegistryRuleSet.Default,
            new RegistryServiceFacts(loggerFactory.CreateLogger<RegistryServiceFacts>()),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            loggerFactory.CreateLogger<RegistryGuard>());
    }

    public RegistryDecision CheckKey(string? keyPath, RegistryView view = RegistryView.Default)
    {
        string input = keyPath ?? string.Empty;
        RegistryVerdict parse = RegistryPath.TryParse(input, view, out RegistryPath? path);
        if (parse != RegistryVerdict.Allowed)
        {
            return Deny(new RegistryDecision { Input = input, Verdict = parse, Path = path });
        }

        RegistryRuleSet.Evaluation evaluation = _rules.Evaluate(path!.Root, path.Segments);
        (RegistryVerdict verdict, string? rule) = evaluation switch
        {
            { Deepest.Kind: ProtectionKind.Tree } => (RegistryVerdict.ProtectedTree, evaluation.Deepest.ToString()),
            { Deepest: not null, DeepestIsSelfMatch: true } => (RegistryVerdict.ProtectedSelf, evaluation.Deepest.ToString()),
            { ContainedRule: not null } => (RegistryVerdict.ContainsProtected, evaluation.ContainedRule.ToString()),
            _ => CheckServiceKey(path),
        };

        var decision = new RegistryDecision { Input = input, Verdict = verdict, Path = path, MatchedRule = rule };
        return decision.IsAllowed ? decision : Deny(decision);
    }

    public RegistryDecision CheckValue(string? keyPath, string? valueName, RegistryView view = RegistryView.Default)
    {
        string input = keyPath ?? string.Empty;
        string name = valueName ?? string.Empty;

        RegistryVerdict parse = RegistryPath.TryParse(input, view, out RegistryPath? path);
        if (parse != RegistryVerdict.Allowed)
        {
            return Deny(new RegistryDecision { Input = input, ValueName = name, Verdict = parse, Path = path });
        }

        RegistryRuleSet.Evaluation evaluation = _rules.Evaluate(path!.Root, path.Segments);
        (RegistryVerdict verdict, string? rule) = evaluation switch
        {
            { Deepest.Kind: ProtectionKind.Tree } => (RegistryVerdict.ProtectedTree, evaluation.Deepest.ToString()),
            { Deepest: not null, DeepestIsSelfMatch: true } when name.Length == 0 =>
                (RegistryVerdict.ProtectedDefaultValue, evaluation.Deepest.ToString()),
            _ when _rules.FindProtectedValue(path.Root, path.Segments, name) is { } protectedValue =>
                (RegistryVerdict.ProtectedValue, protectedValue.Reason),
            _ => CheckServiceKey(path),
        };

        var decision = new RegistryDecision { Input = input, ValueName = name, Verdict = verdict, Path = path, MatchedRule = rule };
        return decision.IsAllowed ? decision : Deny(decision);
    }

    public bool IsContentRevertible(string? keyPath, string? valueName) =>
        RegistryPath.TryParse(keyPath ?? string.Empty, RegistryView.Default, out RegistryPath? path) == RegistryVerdict.Allowed
        && _rules.FindProtectedValue(path!.Root, path.Segments, valueName ?? string.Empty) is { ContentRevertible: true };

    public RegistryDecision CheckRevert(string? keyPath, string? valueName, RegistryValueKind previous, RegistryValueKind? current)
    {
        RegistryDecision decision = CheckValue(keyPath, valueName);
        if (decision.IsAllowed || !IsContentRevertible(keyPath, valueName))
        {
            return decision;
        }

        bool strings = IsString(previous) && (current is null || IsString(current.Value));
        return strings
            ? decision with { Verdict = RegistryVerdict.Allowed, MatchedRule = "revert only: " + decision.MatchedRule }
            : Deny(decision with { Verdict = RegistryVerdict.RevertNotString });

        static bool IsString(RegistryValueKind kind) => kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
    }

    private (RegistryVerdict Verdict, string? Detail) CheckServiceKey(RegistryPath path)
    {
        IReadOnlyList<string> s = path.Segments;
        if (path.Root != RegistryRoot.Machine || s.Count < 4
            || !Is(s[0], "SYSTEM") || !Is(s[1], "CurrentControlSet") || !Is(s[2], "Services"))
        {
            return (RegistryVerdict.Allowed, null);
        }

        ServiceFacts? facts = _services.TryGet(s[3]);
        if (facts is null)
        {
            return (RegistryVerdict.Allowed, null);
        }

        if (!facts.IsReadable || facts.StartType is null)
        {
            return (RegistryVerdict.ServiceUnverifiable, $"service {facts.Name}: start type unknown");
        }

        if (facts.StartType is 0 or 1)
        {
            return (RegistryVerdict.ServiceBootOrSystemStart, $"service {facts.Name}: start type {facts.StartType}");
        }

        string? image = ServiceImagePath.Resolve(facts.ImagePath, _windowsDirectory);
        if (image is null)
        {
            return (RegistryVerdict.ServiceUnverifiable, $"service {facts.Name}: image path unknown");
        }

        return PathSyntax.IsSameOrUnder(image, _windowsDirectory, out _)
            ? (RegistryVerdict.ServiceImageInWindows, $"service {facts.Name}: image {image}")
            : (RegistryVerdict.Allowed, null);
    }

    private RegistryDecision Deny(RegistryDecision decision)
    {
        CoreLog.RegistryDenied(_logger, decision.ValueName is null ? "key" : "value", decision.Verdict, decision.Input, decision.MatchedRule);
        return decision;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
