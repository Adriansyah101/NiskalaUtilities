using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Scanner.Leftovers;

public sealed class LeftoverScanner(
    PathGuard pathGuard,
    RegistryGuard registryGuard,
    IFileTrustProbe trust,
    FileLeftoverProbe files,
    RegistryLeftoverProbe registry,
    FirewallLeftoverProbe firewall,
    SteamLeftoverProbe steam,
    ScheduledTaskLeftoverProbe tasks,
    PackageLeftoverProbe packages,
    ICommandEnvironment environment,
    ILogger<LeftoverScanner> logger,
    TimeProvider? time = null)
{
    private static readonly LeftoverSignal[] InheritedSignals =
        [LeftoverSignal.InstallerIdentity, LeftoverSignal.InFingerprint, LeftoverSignal.PointsToFingerprint];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LeftoverReport Scan(InstalledApp app, Fingerprint fingerprint, IReadOnlyList<InstalledApp> installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(installed);
        long start = Stopwatch.GetTimestamp();
        var notes = new List<string>(fingerprint.Notes);
        var context = new ScanContext(app, fingerprint, environment);

        if (installed.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add("The application is still installed: what it uses now is listed as well.");
        }

        if (!Environment.IsPrivilegedProcess)
        {
            notes.Add("Not elevated: other accounts' folders and some machine-wide keys may be unreadable.");
        }

        if (context.Tokens.Product.Count == 0)
        {
            notes.Add("The name gives no usable product token; only fingerprint evidence is used.");
        }

        var candidates = new CandidateSet();
        files.Find(context, candidates, cancellationToken);
        steam.Find(context, candidates);
        packages.Find(context, candidates);
        registry.Find(context, candidates, notes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        firewall.Find(context, candidates, notes);
        tasks.Find(context, candidates, notes);

        List<OtherInstall> others = Others(app, installed);
        var excluded = new List<ExcludedLeftover>();
        var kept = new List<LeftoverCandidate>();
        foreach (LeftoverCandidate candidate in candidates.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exclude(candidate, others) is { } exclusion)
            {
                excluded.Add(exclusion);
            }
            else
            {
                kept.Add(candidate);
            }
        }

        var scorer = new ConfidenceScorer(app.InstallDate, [.. others.Select(o => new OtherApp(o.Name, o.Tokens))]);
        List<LeftoverItem> items = [.. Collapse(kept)
            .Select(Measured)
            .Select(scorer.Score)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)];

        var report = new LeftoverReport
        {
            AppId = app.Id,
            AppName = app.DisplayName,
            ScannedAt = _time.GetUtcNow(),
            Elapsed = Stopwatch.GetElapsedTime(start),
            Items = items,
            Excluded = excluded,
            Notes = notes,
        };

        ScannerLog.LeftoversScanned(
            logger,
            app.Id,
            items.Count,
            items.Count(i => i.Tier == ConfidenceTier.Safe),
            items.Count(i => i.Tier == ConfidenceTier.Review),
            excluded.Count,
            (long)report.Elapsed.TotalMilliseconds);
        return report;
    }

    internal static IEnumerable<LeftoverCandidate> Collapse(IReadOnlyList<LeftoverCandidate> candidates)
    {
        List<LeftoverCandidate> folders = [.. candidates.Where(c => c.Kind == LeftoverKind.Folder).OrderBy(c => c.Path.Length)];
        var inherited = new Dictionary<LeftoverCandidate, List<(LeftoverSignal, string)>>();
        var nested = new HashSet<LeftoverCandidate>();
        foreach (LeftoverCandidate candidate in candidates.Where(c => c.Kind is LeftoverKind.Folder or LeftoverKind.File))
        {
            LeftoverCandidate? parent = folders.FirstOrDefault(f => !ReferenceEquals(f, candidate)
                && f.Path.Length < candidate.Path.Length
                && PathSyntax.IsSameOrUnder(candidate.Path, f.Path, out _));
            if (parent is null)
            {
                continue;
            }

            nested.Add(candidate);
            if (!inherited.TryGetValue(parent, out List<(LeftoverSignal, string)>? evidence))
            {
                inherited[parent] = evidence = [];
            }

            evidence.AddRange(candidate.Evidence
                .Where(e => InheritedSignals.Contains(e.Signal))
                .Select(e => (e.Signal, $"holds {Path.GetFileName(candidate.Path)}: {e.Detail}")));
        }

        foreach (LeftoverCandidate candidate in candidates.Where(c => !nested.Contains(c)))
        {
            yield return inherited.TryGetValue(candidate, out List<(LeftoverSignal, string)>? extra)
                ? candidate with { Evidence = [.. candidate.Evidence, .. extra] }
                : candidate;
        }
    }

    private static List<OtherInstall> Others(InstalledApp app, IReadOnlyList<InstalledApp> installed) =>
    [
        .. installed
            .Where(o => !o.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                && o.DuplicateOfId != app.Id
                && app.DuplicateOfId != o.Id
                && (app.MsiProductCode is null || o.MsiProductCode != app.MsiProductCode)
                && (app.RegistryKey is null || !app.RegistryKey.Equals(o.RegistryKey, StringComparison.OrdinalIgnoreCase)))
            .Select(o => new OtherInstall(
                o.DisplayName ?? o.Id,
                TokenNormalizer.For(o),
                o.InstallLocationGuard == PathVerdict.Allowed && PathSyntax.TryNormalize(o.InstallLocation, out string root) == PathVerdict.Allowed ? root : null)),
    ];

    private ExcludedLeftover? Exclude(LeftoverCandidate candidate, List<OtherInstall> others)
    {
        if (candidate.Kind is LeftoverKind.Folder or LeftoverKind.File)
        {
            PathDecision decision = pathGuard.Check(candidate.Path);
            if (!decision.IsAllowed)
            {
                return Excluded(candidate, ExclusionReason.Protected, decision.ToString());
            }

            bool identified = candidate.Evidence.Any(e => e.Signal is LeftoverSignal.InstallerIdentity or LeftoverSignal.SteamAppId);
            foreach (string form in decision.AllowedForms)
            {
                if (others.FirstOrDefault(o => o.Root is { } root
                        && (PathSyntax.IsSameOrUnder(root, form, out _) || (!identified && PathSyntax.IsSameOrUnder(form, root, out _)))) is { } owner)
                {
                    return Excluded(candidate, ExclusionReason.OtherAppFolder, $"install folder of {owner.Name}: {owner.Root}");
                }
            }

            return trust.Check(candidate.Path, candidate.Kind == LeftoverKind.Folder) is { } reason
                ? Excluded(candidate, reason, reason == ExclusionReason.MicrosoftSigned ? "Microsoft-signed program files" : "owned by TrustedInstaller")
                : null;
        }

        if (candidate.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue)
        {
            RegistryDecision registryDecision = candidate.Kind == LeftoverKind.RegistryKey
                ? registryGuard.CheckKey(candidate.Path)
                : registryGuard.CheckValue(candidate.Path, candidate.ValueName);
            if (!registryDecision.IsAllowed)
            {
                return Excluded(candidate, ExclusionReason.Protected, registryDecision.ToString());
            }
        }

        if (candidate.ReferencedFile is not { } file || PathSyntax.TryNormalize(environment.ExpandVariables(file), out string target) != PathVerdict.Allowed)
        {
            return null;
        }

        if (others.FirstOrDefault(o => o.Root is { } root && PathSyntax.IsSameOrUnder(target, root, out _)) is { } user)
        {
            return Excluded(candidate, ExclusionReason.OtherAppFolder, $"{target} belongs to {user.Name}");
        }

        return File.Exists(target) && trust.Check(target, isDirectory: false) == ExclusionReason.MicrosoftSigned
            ? Excluded(candidate, ExclusionReason.MicrosoftSigned, $"{target} is Microsoft-signed")
            : null;
    }

    private static LeftoverCandidate Measured(LeftoverCandidate candidate)
    {
        try
        {
            switch (candidate.Kind)
            {
                case LeftoverKind.Folder:
                    (int count, long bytes) = FileLeftoverProbe.Measure(candidate.Path);
                    return candidate with { Files = count, Bytes = bytes };
                case LeftoverKind.File:
                    return candidate with { Files = 1, Bytes = new FileInfo(candidate.Path).Length };
                default:
                    return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return candidate;
        }
    }

    private static ExcludedLeftover Excluded(LeftoverCandidate candidate, ExclusionReason reason, string detail) =>
        new(candidate.Kind, candidate.Path, candidate.ValueName, reason, detail);

    private sealed record OtherInstall(string Name, AppTokens Tokens, string? Root);
}
