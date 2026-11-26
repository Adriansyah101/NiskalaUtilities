using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall;
using Niskala.Uninstall.Batch;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;

namespace Niskala.Cli.Commands;

internal sealed record BatchAppOutput(InstalledApp App, BatchBlock Block, string? Detail, bool? Removed, string? Outcome);

internal sealed record BatchOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    IReadOnlyList<BatchAppOutput> Apps,
    IReadOnlyList<LeftoverItem> Leftovers,
    IReadOnlyList<TransactionRecord> Transactions);

internal static class BatchText
{
    private const int SchemaVersion = 1;

    public static async Task<int> RunAsync(NiskalaHost host, IReadOnlyList<InstalledApp> apps, UninstallMode mode, bool restorePoint, bool permanent, bool dryRun, bool assumeYes, bool asJson, CancellationToken cancellationToken)
    {
        TextWriter o = asJson ? Console.Error : Console.Out;
        BatchUninstaller batch = host.CreateBatchUninstaller();
        IReadOnlyList<BatchItem> items = batch.Plan(apps, mode, cancellationToken);
        WritePlan(o, items);
        int runnable = items.Count(i => i.CanRun);
        if (runnable == 0 || dryRun)
        {
            o.WriteLine(runnable == 0 ? "Blocked: nothing was run." : "Dry run: nothing was run.");
            WriteJson(asJson, items, [], dryRun, null, []);
            return runnable == 0 ? ExitCodes.Failure : ExitCodes.Success;
        }

        if (await Confirmation.RequireAsync(string.Create(CultureInfo.InvariantCulture, $"Uninstall {runnable} applications one after another?"), assumeYes, cancellationToken).ConfigureAwait(false) is { } declined)
        {
            return declined;
        }

        IReadOnlyList<BatchResult> results;
        try
        {
            results = await batch.RunAsync(items, restorePoint, new ConsoleProgress(Console.Error, items), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Stopped waiting. The current uninstaller keeps running; the rest of the queue was not started.").ConfigureAwait(false);
            return ExitCodes.Cancelled;
        }

        o.WriteLine();
        foreach (BatchResult result in results)
        {
            o.WriteLine($"  {(result.Removed ? "removed" : "kept   ")}  {Name(result.Item.App)}: {Outcome(result) ?? Blocked(result.Item)}");
        }

        IReadOnlyList<InstalledApp> installed = host.CreateInventoryCollector().Collect(cancellationToken: cancellationToken).Apps;
        BatchReview review = batch.Scan(results, installed, cancellationToken);
        IReadOnlyList<TransactionRecord> transactions = await CleanAsync(host, batch, review, permanent, assumeYes, o, cancellationToken).ConfigureAwait(false);
        WriteJson(asJson, items, results, dryRun, review, transactions);
        bool allRemoved = results.All(r => r.Removed);
        return !allRemoved ? ExitCodes.Failure
            : transactions.Any(t => CleanText.ExitCode(t) != ExitCodes.Success) ? ExitCodes.Failure
            : ExitCodes.Success;
    }

    private static async Task<IReadOnlyList<TransactionRecord>> CleanAsync(NiskalaHost host, BatchUninstaller batch, BatchReview review, bool permanent, bool assumeYes, TextWriter o, CancellationToken cancellationToken)
    {
        List<LeftoverItem> safe = [.. review.Items.Where(i => i.Tier == ConfidenceTier.Safe)];
        List<LeftoverItem> toReview = [.. review.Items.Where(i => i.Tier == ConfidenceTier.Review)];
        o.WriteLine();
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Leftovers of the whole queue ({safe.Count} safe, {toReview.Count} to review):"));
        foreach (string note in review.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        foreach (LeftoverItem item in toReview)
        {
            o.WriteLine($"  review {item.Score,3}  {CleanText.Describe(item.Kind, item.Path, item.ValueName)}  ({Name(review.OwnerOf(item)!.App)})");
        }

        if (toReview.Count > 0)
        {
            o.WriteLine("  Items to review stay. Remove them per app with: niskala clean <app> --review");
        }

        if (safe.Count == 0)
        {
            return [];
        }

        LeftoverCleaner cleaner = host.CreateLeftoverCleaner();
        CleanText.WritePlan(o, safe, cleaner.PredictEmptyParents(safe), permanent, restorePoint: false);
        if (await Confirmation.RequireAsync(string.Create(CultureInfo.InvariantCulture, $"Remove {safe.Count} leftovers?"), assumeYes, cancellationToken).ConfigureAwait(false) is not null)
        {
            return [];
        }

        var options = new CleanOptions { Permanent = permanent, CreateRestorePoint = false };
        var transactions = new List<TransactionRecord>();
        foreach (CleanResult result in await batch.CleanAsync(review, safe, options, new CleanText.Progress(Console.Error), cancellationToken).ConfigureAwait(false))
        {
            TransactionRecord transaction = await CleanText.OfferEmptyFoldersAsync(cleaner, result, assumeYes, o, cancellationToken).ConfigureAwait(false);
            CleanText.WriteTransaction(o, transaction);
            transactions.Add(transaction);
        }

        return transactions;
    }

    private static void WritePlan(TextWriter o, IReadOnlyList<BatchItem> items)
    {
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Batch Remove: {items.Count} applications, in this order"));
        for (int i = 0; i < items.Count; i++)
        {
            BatchItem item = items[i];
            string how = item switch
            {
                { CanRun: false } => $"blocked: {Blocked(item)}",
                { Steam: not null } => "Steam game, removed directly",
                { Plan.Resolution.Command: { } command } => command.CommandLine,
                { Plan.Resolution.PackageFullName: { } package } => $"remove package {package}",
                _ => "nothing to run",
            };
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}. {Name(item.App)}{(item.App.DisplayVersion is { Length: > 0 } version ? $" {version}" : string.Empty)}"));
            o.WriteLine($"     {how}");
        }
    }

    private static string Blocked(BatchItem item) => item.Block switch
    {
        BatchBlock.NoCommand => "no uninstall command",
        BatchBlock.SteamRunning => "Steam is running; close it, or remove this game on its own with --close-steam",
        BatchBlock.NoSteamPlan => $"no Steam plan: {item.Detail}",
        BatchBlock.Preflight => item.Detail ?? "the preflight checks block it",
        _ => "not run",
    };

    private static string? Outcome(BatchResult result) => result switch
    {
        { Uninstall: { } ran } => ran.Outcome.ToString(),
        { Steam: { } steam } => steam.Outcome.ToString(),
        _ => null,
    };

    private static string Name(InstalledApp app) => app.DisplayName ?? app.Id;

    private static void WriteJson(bool asJson, IReadOnlyList<BatchItem> items, IReadOnlyList<BatchResult> results, bool dryRun, BatchReview? review, IReadOnlyList<TransactionRecord> transactions)
    {
        if (!asJson)
        {
            return;
        }

        List<BatchAppOutput> apps = [.. items.Select((item, i) =>
        {
            BatchResult? result = i < results.Count ? results[i] : null;
            return new BatchAppOutput(item.App, item.Block, item.Detail, result is { Item.CanRun: true } ? result.Removed : null, result is null ? null : Outcome(result));
        })];
        var output = new BatchOutput(SchemaVersion, DateTimeOffset.Now, dryRun, apps, review?.Items ?? [], transactions);
        Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.BatchOutput));
    }

    private sealed class ConsoleProgress(TextWriter writer, IReadOnlyList<BatchItem> items) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            string name = Name(items[value.Index].App);
            string? line = value switch
            {
                { Step: BatchStep.Started, Uninstall: null, Clean: null } => $"[{value.Index + 1}/{items.Count}] {name}...",
                { Step: BatchStep.Started, Uninstall.Event: { Kind: JobEventKind.Started } e } => $"  + {e.ProcessId} {Path.GetFileName(e.ImagePath)}",
                { Step: BatchStep.Started, Uninstall.Event: { Kind: JobEventKind.Exited or JobEventKind.ExitedAbnormally } e } => $"  - {e.ProcessId} {Path.GetFileName(e.ImagePath)}",
                { Step: BatchStep.Started, Uninstall.Stage: UninstallStage.Verifying } => "  verifying...",
                { Step: BatchStep.Skipped } => $"[{value.Index + 1}/{items.Count}] {name}: skipped",
                _ => null,
            };

            if (line is not null)
            {
                writer.WriteLine(line);
            }
        }
    }
}
