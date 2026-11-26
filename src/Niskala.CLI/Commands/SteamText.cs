using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Scanner.Leftovers;
using Niskala.Scanner.Steam;
using Niskala.Uninstall.Steam;

namespace Niskala.Cli.Commands;

internal sealed record SteamOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    InstalledApp App,
    SteamRemoval Removal,
    SteamGame? Game,
    IReadOnlyList<LeftoverItem> Items,
    SteamOutcome? Outcome,
    TransactionRecord? Transaction,
    LeftoverReport? Leftovers,
    TransactionRecord? LeftoverTransaction);

internal static class SteamText
{
    private const int SchemaVersion = 1;

    public static async Task<int> RunAsync(NiskalaHost host, InstalledApp app, SteamRemoval removal, SteamOptions options, bool dryRun, bool assumeYes, bool asJson, CancellationToken cancellationToken)
    {
        TextWriter o = asJson ? Console.Error : Console.Out;
        SteamUninstaller steam = host.CreateSteamUninstaller();
        if (steam.Plan(app, out string? problem, cancellationToken) is not { } plan)
        {
            await Console.Error.WriteLineAsync($"{app.DisplayName ?? app.Id}: {problem}. Without --via-steam nothing can be removed directly.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        if (plan.Game is null && removal == SteamRemoval.ViaSteam)
        {
            await Console.Error.WriteLineAsync($"{app.DisplayName ?? app.Id}: its files are gone, so Steam cannot uninstall it. Without --via-steam, Niskala removes what Steam keeps of it.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        WritePlan(o, plan, removal, options);
        bool blocked = plan.SteamRunning && removal == SteamRemoval.Direct;
        if (blocked)
        {
            o.WriteLine("Steam is running. Add --close-steam to close it first, or --via-steam to let Steam uninstall the game.");
        }

        if (dryRun || blocked)
        {
            o.WriteLine(dryRun ? "Dry run: nothing was removed." : "Nothing was removed.");
            WriteJson(asJson, new SteamOutput(SchemaVersion, DateTimeOffset.Now, dryRun, plan.App, removal, plan.Game, plan.Items, null, null, null, null));
            return blocked && !dryRun ? ExitCodes.Usage : ExitCodes.Success;
        }

        string name = plan.Game?.Manifest.Name ?? app.DisplayName ?? app.Id;
        string question = removal == SteamRemoval.ViaSteam
            ? $"Ask Steam to uninstall {name}?"
            : $"Remove {name} without Steam{(plan.SteamRunning ? " after closing Steam" : string.Empty)}?";
        if (await Confirmation.RequireAsync(question, assumeYes, cancellationToken).ConfigureAwait(false) is { } declined)
        {
            return declined;
        }

        SteamResult result = await steam.ExecuteAsync(plan, options with { Removal = removal }, new CleanText.Progress(Console.Error), cancellationToken).ConfigureAwait(false);
        TransactionRecord? transaction = null;
        LeftoverReport? leftovers = null;
        TransactionRecord? leftoverTransaction = null;
        switch (result.Outcome)
        {
            case SteamOutcome.Removed:
                transaction = await CleanText.OfferEmptyFoldersAsync(host.CreateLeftoverCleaner(), result.Clean!, assumeYes, o, cancellationToken).ConfigureAwait(false);
                CleanText.WriteTransaction(o, transaction);
                (leftovers, leftoverTransaction) = await CleanLeftoversAsync(host, plan, options, assumeYes, o, cancellationToken).ConfigureAwait(false);
                break;
            case SteamOutcome.HandedToSteam:
                o.WriteLine($"Steam takes it from here. Once it has finished: niskala leftovers \"{app.Id}\", then niskala clean \"{app.Id}\"");
                break;
            case SteamOutcome.SteamRunning:
                o.WriteLine("Steam is still running; nothing was removed.");
                break;
            case SteamOutcome.SteamNotStarted:
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"steam.exe could not be started (Win32 error {result.Error}); nothing was removed."));
                break;
        }

        WriteJson(asJson, new SteamOutput(SchemaVersion, DateTimeOffset.Now, dryRun, plan.App, removal, plan.Game, plan.Items, result.Outcome, transaction, leftovers, leftoverTransaction));
        return result.Outcome switch
        {
            SteamOutcome.Removed => CleanText.ExitCode(transaction!),
            SteamOutcome.HandedToSteam => ExitCodes.Success,
            _ => ExitCodes.Failure,
        };
    }

    private static void WritePlan(TextWriter o, SteamPlan plan, SteamRemoval removal, SteamOptions options)
    {
        if (plan.Game is { } game)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{game.Manifest.Name} (Steam app {plan.AppId}, {TextFormat.Bytes(game.Manifest.SizeOnDisk)})"));
            o.WriteLine($"  Library    {game.Library}");
        }
        else
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{plan.App.DisplayName ?? plan.App.Id} (Steam app {plan.AppId})"));
            o.WriteLine("  Library    none: the game's files are gone, Steam still keeps records of it");
        }

        o.WriteLine($"  Steam      {(plan.SteamRunning ? "running" : "not running")}");
        o.WriteLine(removal switch
        {
            SteamRemoval.ViaSteam => "  Mode       steam://uninstall: Steam removes the game and can sync Steam Cloud first",
            SteamRemoval.CloseSteam => "  Mode       close Steam, then remove directly",
            _ => "  Mode       remove directly; Steam stays closed",
        });
        if (removal != SteamRemoval.ViaSteam)
        {
            CleanText.WritePlan(o, plan.Items, [], options.Permanent, options.CreateRestorePoint);
        }
    }

    private static async Task<(LeftoverReport Report, TransactionRecord? Transaction)> CleanLeftoversAsync(
        NiskalaHost host, SteamPlan plan, SteamOptions options, bool assumeYes, TextWriter o, CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledApp> installed = host.CreateInventoryCollector().Collect(cancellationToken: cancellationToken).Apps;
        LeftoverReport report = host.CreateLeftoverScanner().Scan(plan.App, plan.Fingerprint, installed, cancellationToken);
        IReadOnlyList<LeftoverItem> safe = CleanText.Selected(report, includeReview: false);
        List<LeftoverItem> review = [.. report.Items.Where(i => i.Tier == ConfidenceTier.Review)];
        o.WriteLine();
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Leftovers ({safe.Count} safe, {review.Count} to review):"));
        foreach (LeftoverItem item in review)
        {
            o.WriteLine($"  review {item.Score,3}  {CleanText.Describe(item.Kind, item.Path, item.ValueName)}");
        }

        if (review.Count > 0)
        {
            o.WriteLine($"  Items to review stay, saves among them. Remove them with: niskala clean \"{plan.App.Id}\" --review");
        }

        if (safe.Count == 0)
        {
            return (report, null);
        }

        LeftoverCleaner cleaner = host.CreateLeftoverCleaner();
        CleanText.WritePlan(o, safe, cleaner.PredictEmptyParents(safe), options.Permanent, restorePoint: false);
        if (await Confirmation.RequireAsync(string.Create(CultureInfo.InvariantCulture, $"Remove {safe.Count} leftovers?"), assumeYes, cancellationToken).ConfigureAwait(false) is not null)
        {
            return (report, null);
        }

        var cleanOptions = new CleanOptions { Permanent = options.Permanent, CreateRestorePoint = false };
        CleanResult result = await cleaner.CleanAsync(plan.App, plan.Fingerprint, safe, cleanOptions, new CleanText.Progress(Console.Error), cancellationToken).ConfigureAwait(false);
        TransactionRecord transaction = await CleanText.OfferEmptyFoldersAsync(cleaner, result, assumeYes, o, cancellationToken).ConfigureAwait(false);
        CleanText.WriteTransaction(o, transaction);
        return (report, transaction);
    }

    private static void WriteJson(bool asJson, SteamOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.SteamOutput));
        }
    }
}
