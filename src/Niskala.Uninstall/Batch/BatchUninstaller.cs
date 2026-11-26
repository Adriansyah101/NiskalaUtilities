using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Steam;

namespace Niskala.Uninstall.Batch;

public enum BatchBlock
{
    None,
    NoCommand,
    Preflight,
    NoSteamPlan,
    SteamRunning,
}

public sealed record BatchItem(InstalledApp App, UninstallPlan? Plan, SteamPlan? Steam, BatchBlock Block, string? Detail = null)
{
    public bool CanRun => Block == BatchBlock.None && (Plan is not null || Steam is not null);

    public Fingerprint? Fingerprint => Plan?.Fingerprint ?? Steam?.Fingerprint;
}

public enum BatchStep
{
    Started,
    Finished,
    Failed,
    Skipped,
}

public sealed record BatchProgress(int Index, BatchStep Step, BatchResult? Result = null, UninstallProgress? Uninstall = null, CleanProgress? Clean = null);

public sealed record BatchResult(BatchItem Item, bool Removed, UninstallResult? Uninstall, SteamResult? Steam);

public sealed class BatchUninstaller(Func<bool, UninstallOrchestrator> orchestrators, SteamUninstaller steam, LeftoverScanner scanner, LeftoverCleaner cleaner)
{
    public IReadOnlyList<BatchItem> Plan(IReadOnlyList<InstalledApp> apps, UninstallMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        UninstallOrchestrator planner = orchestrators(false);
        var items = new List<BatchItem>(apps.Count);
        foreach (InstalledApp app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (app.InstallerKind == InstallerKind.Steam)
            {
                SteamPlan? game = steam.Plan(app, out string? problem, cancellationToken);
                items.Add(game switch
                {
                    null => new BatchItem(app, null, null, BatchBlock.NoSteamPlan, problem),
                    { SteamRunning: true } => new BatchItem(app, null, game, BatchBlock.SteamRunning),
                    _ => new BatchItem(app, null, game, BatchBlock.None),
                });
                continue;
            }

            UninstallPlan plan = planner.Plan(app, mode, cancellationToken);
            items.Add(!plan.Resolution.CanRun ? new BatchItem(app, plan, null, BatchBlock.NoCommand)
                : !plan.Preflight.CanProceed ? new BatchItem(app, plan, null, BatchBlock.Preflight, plan.Preflight.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Blocking)?.Message)
                : new BatchItem(app, plan, null, BatchBlock.None));
        }

        return items;
    }

    public async Task<IReadOnlyList<BatchResult>> RunAsync(IReadOnlyList<BatchItem> items, bool restorePoint, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<BatchResult>(items.Count);
        bool checkpointed = false;
        for (int index = 0; index < items.Count; index++)
        {
            BatchItem item = items[index];
            if (!item.CanRun)
            {
                var skipped = new BatchResult(item, false, null, null);
                results.Add(skipped);
                progress?.Report(new BatchProgress(index, BatchStep.Skipped, skipped));
                continue;
            }

            progress?.Report(new BatchProgress(index, BatchStep.Started));
            int at = index;
            bool point = restorePoint && !checkpointed;
            BatchResult result;
            if (item.Steam is { } game)
            {
                var options = new SteamOptions { Removal = SteamRemoval.Direct, CreateRestorePoint = point };
                var clean = new Progress<CleanProgress>(p => progress?.Report(new BatchProgress(at, BatchStep.Started, Clean: p)));
                SteamResult removed = await steam.ExecuteAsync(game, options, clean, cancellationToken).ConfigureAwait(false);
                result = new BatchResult(item, removed.Outcome == SteamOutcome.Removed, null, removed);
            }
            else
            {
                var uninstall = new Progress<UninstallProgress>(p => progress?.Report(new BatchProgress(at, BatchStep.Started, Uninstall: p)));
                UninstallResult ran = await orchestrators(point).ExecuteAsync(item.Plan!, uninstall, cancellationToken).ConfigureAwait(false);
                result = new BatchResult(item, Finished(ran.Outcome), ran, null);
            }

            checkpointed = true;
            results.Add(result);
            progress?.Report(new BatchProgress(index, result.Removed ? BatchStep.Finished : BatchStep.Failed, result));
        }

        return results;
    }

    public BatchReview Scan(IReadOnlyList<BatchResult> results, IReadOnlyList<InstalledApp> installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(installed);
        var scans = new List<(BatchItem, LeftoverReport)>();
        foreach (BatchResult result in results.Where(r => r.Removed && r.Item.Fingerprint is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scans.Add((result.Item, scanner.Scan(result.Item.App, result.Item.Fingerprint!, installed, cancellationToken)));
        }

        return BatchReview.Merge(scans);
    }

    public async Task<IReadOnlyList<CleanResult>> CleanAsync(BatchReview review, IReadOnlyList<LeftoverItem> chosen, CleanOptions options, IProgress<CleanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<CleanResult>();
        foreach ((BatchItem owner, IReadOnlyList<LeftoverItem> items) in review.ByOwner(chosen))
        {
            results.Add(await cleaner.CleanAsync(owner.App, owner.Fingerprint!, items, options, progress, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public static bool Finished(UninstallOutcome outcome) =>
        outcome is UninstallOutcome.Removed or UninstallOutcome.PendingReboot or UninstallOutcome.StaleEntry or UninstallOutcome.Unverified;
}

public sealed class BatchReview
{
    private readonly Dictionary<LeftoverItem, BatchItem> _owners;

    private BatchReview(IReadOnlyList<LeftoverItem> items, Dictionary<LeftoverItem, BatchItem> owners, IReadOnlyList<string> notes, int excluded)
    {
        Items = items;
        _owners = owners;
        Notes = notes;
        Excluded = excluded;
    }

    public IReadOnlyList<LeftoverItem> Items { get; }

    public IReadOnlyList<string> Notes { get; }

    public int Excluded { get; }

    public static BatchReview Merge(IReadOnlyList<(BatchItem Item, LeftoverReport Report)> scans)
    {
        ArgumentNullException.ThrowIfNull(scans);
        var best = new Dictionary<(LeftoverKind, string, string?), (LeftoverItem Item, BatchItem Owner)>();
        var notes = new List<string>();
        foreach ((BatchItem owner, LeftoverReport report) in scans)
        {
            string name = owner.App.DisplayName ?? owner.App.Id;
            notes.AddRange(report.Notes.Select(n => $"{name}: {n}"));
            foreach (LeftoverItem item in report.Items)
            {
                var key = (item.Kind, item.Path.ToUpperInvariant(), item.ValueName?.ToUpperInvariant());
                if (!best.TryGetValue(key, out (LeftoverItem Item, BatchItem Owner) found) || item.Score > found.Item.Score)
                {
                    best[key] = (item, owner);
                }
            }
        }

        var owners = new Dictionary<LeftoverItem, BatchItem>(ReferenceEqualityComparer.Instance);
        foreach ((LeftoverItem item, BatchItem owner) in best.Values)
        {
            owners[item] = owner;
        }

        List<LeftoverItem> items = [.. owners.Keys.OrderByDescending(i => i.Score).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)];
        return new BatchReview(items, owners, notes, scans.Sum(s => s.Report.Excluded.Count));
    }

    public BatchItem? OwnerOf(LeftoverItem item) => _owners.GetValueOrDefault(item);

    public IReadOnlyList<(BatchItem Owner, IReadOnlyList<LeftoverItem> Items)> ByOwner(IEnumerable<LeftoverItem> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        return [.. chosen
            .Where(_owners.ContainsKey)
            .GroupBy(i => _owners[i])
            .Select(g => (g.Key, (IReadOnlyList<LeftoverItem>)[.. g]))];
    }
}
