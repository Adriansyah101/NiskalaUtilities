using System.CommandLine;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cli.Commands;

internal sealed record CleanOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    InstalledApp App,
    IReadOnlyList<LeftoverItem> Selected,
    IReadOnlyList<string> EmptyFolders,
    TransactionRecord? Transaction);

internal static class CleanAppCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose)
    {
        var target = new Argument<string>("app") { Description = "Id or name of an application uninstalled with niskala uninstall." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation; empty folders are removed as well." };
        var permanent = new Option<bool>("--permanent") { Description = "Delete files permanently instead of moving them to the Recycle Bin." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be removed; change nothing." };
        var review = new Option<bool>("--review") { Description = "Also remove items in the Review tier." };
        var noRestorePoint = new Option<bool>("--no-restore-point") { Description = "Skip the System Restore point; the registry backup is still made." };
        var json = new Option<bool>("--json") { Description = "Write the plan and the transaction as JSON to stdout; progress goes to stderr." };

        var command = new Command("app", "Remove an uninstalled application's leftovers: Safe items by default, recorded so niskala restore can undo it.")
        {
            target, yes, permanent, dryRun, review, noRestorePoint, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string query = parse.GetValue(target)!;
            bool assumeYes = parse.GetValue(yes);
            bool isPermanent = parse.GetValue(permanent);
            bool isDryRun = parse.GetValue(dryRun);
            bool includeReview = parse.GetValue(review);
            bool restorePoint = !parse.GetValue(noRestorePoint);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("clean app", parse.GetValue(verbose), async (host, token) =>
            {
                IReadOnlyList<InstalledApp> installed = host.CreateInventoryCollector().Collect(cancellationToken: token).Apps;
                IReadOnlyList<FingerprintRecord> records = host.CreateFingerprintStore().LoadAll();
                List<InstalledApp> stored = AppQuery.Find(records.Select(r => r.App), query);
                if (stored.Count != 1)
                {
                    string hint = stored.Count == 0 && AppQuery.Find(installed, query).Count > 0
                        ? "It is still installed: run niskala uninstall first, or niskala force."
                        : "Only applications uninstalled with niskala uninstall have a saved fingerprint.";
                    await AppQuery.ReportNoSingleMatch(query, stored, hint).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                InstalledApp app = stored[0];
                if (installed.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    await Console.Error.WriteLineAsync($"{app.DisplayName ?? app.Id} is still installed: run niskala uninstall first, or niskala force.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                Fingerprint fingerprint = records.First(r => ReferenceEquals(r.App, app)).Fingerprint;
                LeftoverReport report = host.CreateLeftoverScanner().Scan(app, fingerprint, installed, token);
                IReadOnlyList<LeftoverItem> items = CleanText.Selected(report, includeReview);
                LeftoverCleaner cleaner = host.CreateLeftoverCleaner();
                IReadOnlyList<string> predicted = cleaner.PredictEmptyParents(items);

                TextWriter o = asJson ? Console.Error : Console.Out;
                o.WriteLine($"{app.DisplayName ?? app.Id} {app.DisplayVersion}");
                CleanText.WritePlan(o, items, predicted, isPermanent, restorePoint);
                if (isDryRun || items.Count == 0)
                {
                    o.WriteLine(items.Count == 0 ? "Nothing to remove." : "Dry run: nothing was changed.");
                    WriteJson(asJson, new CleanOutput(SchemaVersion, DateTimeOffset.Now, isDryRun, app, items, predicted, null));
                    return ExitCodes.Success;
                }

                if (await Confirmation.RequireAsync($"Remove {items.Count} items?", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                var options = new CleanOptions { Permanent = isPermanent, CreateRestorePoint = restorePoint };
                CleanResult result = await cleaner.CleanAsync(app, fingerprint, items, options, new CleanText.Progress(Console.Error), token).ConfigureAwait(false);
                TransactionRecord transaction = await CleanText.OfferEmptyFoldersAsync(cleaner, result, assumeYes, o, token).ConfigureAwait(false);
                CleanText.WriteTransaction(o, transaction);
                WriteJson(asJson, new CleanOutput(SchemaVersion, DateTimeOffset.Now, false, app, items, result.EmptyParents, transaction));
                return CleanText.ExitCode(transaction);
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteJson(bool asJson, CleanOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.CleanOutput));
        }
    }
}
