using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Scanner.Fingerprints;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Rollback;
using Niskala.Uninstall;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Steam;

namespace Niskala.Cli.Commands;

internal sealed record UninstallOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    InstalledApp App,
    CommandResolution Resolution,
    PreflightReport Preflight,
    Fingerprint Fingerprint,
    UninstallResult? Result,
    TransactionRecord? Rollback);

internal static class UninstallCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose)
    {
        var targets = new Argument<string[]>("apps") { Description = "Id, exact name or part of the name of each installed application; several run one after another, with one leftover review at the end.", Arity = ArgumentArity.OneOrMore };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the command, the preflight checks and the fingerprint; run nothing." };
        var silent = new Option<bool>("--silent") { Description = "Run the uninstaller without its UI where the installer supports it." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var all = new Option<bool>("--all") { Description = "Also match hidden entries." };
        var json = new Option<bool>("--json") { Description = "Write the plan and the result as JSON to stdout; progress goes to stderr." };
        var noRestorePoint = new Option<bool>("--no-restore-point") { Description = "Skip the System Restore point; the registry backup is still made." };
        var noLog = new Option<bool>("--no-log") { Description = "Ignore the install log of this application; leave the leftovers to niskala leftovers." };
        var closeSteam = new Option<bool>("--close-steam") { Description = "Steam games: close Steam if it runs, then remove the game directly." };
        var viaSteam = new Option<bool>("--via-steam") { Description = "Steam games: let Steam uninstall the game (steam://uninstall), for example to sync Steam Cloud first." };
        var permanent = new Option<bool>("--permanent") { Description = "Steam games and leftovers of a batch: delete the files instead of moving them to the Recycle Bin." };

        var command = new Command("uninstall", "Uninstall applications with their own uninstallers, then verify that they are gone.")
        {
            targets, dryRun, silent, yes, all, json, noRestorePoint, noLog, closeSteam, viaSteam, permanent,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(closeSteam) && result.GetValue(viaSteam))
            {
                result.AddError("--close-steam and --via-steam exclude each other.");
            }

            if (result.GetValue(targets) is { Length: > 1 } && (result.GetValue(closeSteam) || result.GetValue(viaSteam)))
            {
                result.AddError("--close-steam and --via-steam apply to one Steam game at a time; a batch removes Steam games directly while Steam is closed.");
            }
        });

        command.SetAction((parse, cancellationToken) =>
        {
            string[] queries = parse.GetValue(targets)!;
            UninstallMode mode = parse.GetValue(silent) ? UninstallMode.Silent : UninstallMode.Interactive;
            bool isDryRun = parse.GetValue(dryRun);
            bool confirmed = parse.GetValue(yes);
            bool includeHidden = parse.GetValue(all);
            bool asJson = parse.GetValue(json);
            bool restorePoint = !parse.GetValue(noRestorePoint);
            bool useLog = !parse.GetValue(noLog);
            SteamRemoval steamRemoval = parse.GetValue(viaSteam) ? SteamRemoval.ViaSteam : parse.GetValue(closeSteam) ? SteamRemoval.CloseSteam : SteamRemoval.Direct;
            var steamOptions = new SteamOptions { Permanent = parse.GetValue(permanent), CreateRestorePoint = restorePoint };
            return CommandRunner.RunAsync("uninstall", parse.GetValue(verbose), async (host, token) =>
            {
                IReadOnlyList<InstalledApp> apps = host.CreateInventoryCollector().Collect(cancellationToken: token).Apps;
                var chosen = new List<InstalledApp>();
                foreach (string query in queries)
                {
                    List<InstalledApp> found = AppQuery.Find(apps.Where(a => includeHidden || a.IsVisible), query);
                    if (found.Count != 1)
                    {
                        await AppQuery.ReportNoSingleMatch(query, found, "Try niskala list, or --all for hidden entries.").ConfigureAwait(false);
                        return ExitCodes.Usage;
                    }

                    if (!chosen.Exists(a => a.Id.Equals(found[0].Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        chosen.Add(found[0]);
                    }
                }

                if (chosen.Count > 1)
                {
                    return await BatchText.RunAsync(host, chosen, mode, restorePoint, steamOptions.Permanent, isDryRun, confirmed, asJson, token).ConfigureAwait(false);
                }

                InstalledApp single = chosen[0];

                if (single.InstallerKind == InstallerKind.Steam)
                {
                    return await SteamText.RunAsync(host, single, steamRemoval, steamOptions, isDryRun, confirmed, asJson, token).ConfigureAwait(false);
                }

                UninstallOrchestrator orchestrator = host.CreateUninstallOrchestrator(restorePoint);
                UninstallPlan plan = orchestrator.Plan(single, mode, token);
                TextWriter log = asJson ? Console.Error : Console.Out;
                InstallLog? installLog = useLog ? host.CreateInstallLogStore().FindForApp(plan.App.Id) : null;
                WritePlan(log, plan, installLog);

                if (!plan.Preflight.CanProceed || isDryRun)
                {
                    log.WriteLine(plan.Preflight.CanProceed ? "Dry run: nothing was run." : "Blocked: nothing was run.");
                    WriteJson(asJson, plan, isDryRun, null, null);
                    return plan.Preflight.CanProceed ? ExitCodes.Success : ExitCodes.Failure;
                }

                if (await Confirmation.RequireAsync($"Uninstall {plan.App.DisplayName ?? plan.App.Id}?", confirmed, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                UninstallResult result;
                try
                {
                    result = await orchestrator.ExecuteAsync(plan, new ConsoleProgress(Console.Error), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync("Stopped waiting. The uninstaller keeps running; check niskala list once it has finished.").ConfigureAwait(false);
                    return ExitCodes.Cancelled;
                }

                bool finished = result.Outcome is UninstallOutcome.Removed or UninstallOutcome.PendingReboot or UninstallOutcome.StaleEntry or UninstallOutcome.Unverified;
                WriteResult(log, plan, result, installLog is not null && finished);
                TransactionRecord? rollback = null;
                if (installLog is not null && finished)
                {
                    (int? refused, rollback) = await RollBackLeftoversAsync(host, installLog, confirmed, log, token).ConfigureAwait(false);
                    if (refused is { } code)
                    {
                        WriteJson(asJson, plan, isDryRun, result, null);
                        return code;
                    }
                }

                WriteJson(asJson, plan, isDryRun, result, rollback);
                return !finished ? ExitCodes.Failure : rollback is null ? ExitCodes.Success : CleanText.ExitCode(rollback);
            }, cancellationToken);
        });

        return command;
    }

    private static async Task<(int? Declined, TransactionRecord? Transaction)> RollBackLeftoversAsync(NiskalaHost host, InstallLog installLog, bool assumeYes, TextWriter o, CancellationToken cancellationToken)
    {
        LeftoverCleaner cleaner = host.CreateLeftoverCleaner();
        RollbackPlan rollback = host.CreateLogRollback(cleaner).Plan(installLog);
        o.WriteLine();
        o.WriteLine($"What is left of the recorded install ({installLog.Id}):");
        if (LogText.IsEmpty(rollback))
        {
            o.WriteLine("  nothing; the uninstaller removed all of it.");
            return (null, null);
        }

        LogText.WritePlan(o, rollback, cleaner.PredictEmptyParents(rollback.Items), permanent: false, restorePoint: false);
        if (await Confirmation.RequireAsync(LogText.Question(rollback), assumeYes, cancellationToken).ConfigureAwait(false) is { } declined)
        {
            return (declined, null);
        }

        var options = new CleanOptions { CreateRestorePoint = false };
        return (null, await LogText.RollBackAsync(host, cleaner, rollback, options, assumeYes, o, cancellationToken).ConfigureAwait(false));
    }

    private static void WritePlan(TextWriter o, UninstallPlan plan, InstallLog? installLog)
    {
        InstalledApp app = plan.App;
        CommandResolution resolution = plan.Resolution;
        o.WriteLine($"{app.DisplayName ?? app.Id} {app.DisplayVersion} ({app.Publisher ?? "unknown publisher"})");
        o.WriteLine($"  Id         {app.Id}");
        o.WriteLine(resolution switch
        {
            { Command: { } c } => $"  Command    {c.CommandLine}{(c.IsSilent ? "  [silent]" : string.Empty)}",
            { PackageFullName: { } package } => $"  Package    remove {package} (PackageManager, current user)",
            _ => $"  Command    none: {resolution.Issue}",
        });

        foreach (string note in resolution.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        foreach (CommandWarning warning in resolution.Warnings)
        {
            o.WriteLine($"  ! {warning.Kind}: {warning.Message}");
        }

        Fingerprint fingerprint = plan.Fingerprint;
        int traces = fingerprint.Services.Count + fingerprint.RunEntries.Count + fingerprint.AppPaths.Count + fingerprint.ScheduledTasks.Count + fingerprint.Shortcuts.Count;
        o.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Fingerprint {fingerprint.Files?.Files ?? 0:N0} files, {traces} system traces{(fingerprint.Roots.Count == 0 ? ", no install folder" : $" in {fingerprint.Roots[^1]}")}"));
        if (installLog is not null)
        {
            o.WriteLine($"  Install log {installLog.Id}: what the recorded install leaves behind is undone after the uninstaller");
        }

        o.WriteLine($"  Preflight  {(plan.Preflight.CanProceed ? "can proceed" : "blocked")} (elevated: {(plan.Preflight.IsElevated ? "yes" : "no")})");
        foreach (PreflightIssue issue in plan.Preflight.Issues)
        {
            o.WriteLine($"    [{issue.Severity}] {issue.Check}: {issue.Message}");
        }
    }

    private static void WriteResult(TextWriter o, UninstallPlan plan, UninstallResult result, bool rollingBack)
    {
        o.WriteLine();
        o.WriteLine($"Outcome: {result.Outcome}{(result.RebootRequired ? " (restart required)" : string.Empty)}");
        if (result.ExitCode is { } code)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Exit code  {code} ({result.ExitMeaning})"));
        }

        if (result.Run is { } run)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Finished   {run.Reason} after {run.Elapsed.TotalSeconds:0.0} s, {run.Events.Count(e => e.Kind == JobEventKind.Started)} processes"));
        }

        if (result.Verification is { } verification)
        {
            o.WriteLine($"  Entry      {Presence(verification.KeyPresent)}; Windows Installer {Presence(verification.ProductRegistered)}; package {Presence(verification.PackagePresent)}");
            if (verification.Folder is { } folder)
            {
                o.WriteLine(folder.Exists ? $"  Folder     still there, {folder.Files}{(folder.Truncated ? "+" : string.Empty)} files" : "  Folder     removed");
            }
        }

        foreach (string note in (result.Checkpoint?.Notes ?? []).Concat(result.Notes))
        {
            o.WriteLine($"  note: {note}");
        }

        if (!rollingBack && result.FingerprintFile is not null && result.Outcome is not (UninstallOutcome.Blocked or UninstallOutcome.NotStarted))
        {
            o.WriteLine($"Leftovers: niskala leftovers \"{plan.App.Id}\"");
        }
    }

    private static string Presence(bool? present) => present switch
    {
        null => "-",
        true => "present",
        false => "gone",
    };

    private static void WriteJson(bool asJson, UninstallPlan plan, bool dryRun, UninstallResult? result, TransactionRecord? rollback)
    {
        if (!asJson)
        {
            return;
        }

        Fingerprint shown = plan.Fingerprint.Files is null ? plan.Fingerprint : plan.Fingerprint with { Files = plan.Fingerprint.Files with { Entries = [] } };
        var output = new UninstallOutput(SchemaVersion, DateTimeOffset.Now, dryRun, plan.App, plan.Resolution, plan.Preflight, shown, result, rollback);
        Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.UninstallOutput));
    }

    private sealed class ConsoleProgress(TextWriter writer) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value)
        {
            string? line = value switch
            {
                { Event: { Kind: JobEventKind.Started } e } => $"  + {e.ProcessId} {Name(e)}",
                { Event: { Kind: JobEventKind.Exited } e } => $"  - {e.ProcessId} {Name(e)}",
                { Event: { Kind: JobEventKind.ExitedAbnormally } e } => $"  ! {e.ProcessId} {Name(e)} ended abnormally",
                { Event.Kind: JobEventKind.Empty } => "  job empty, waiting for the registry and the folder to settle",
                { Stage: UninstallStage.Guardian } => "Guardian checkpoint...",
                { Stage: UninstallStage.Running } => "Running the uninstaller...",
                { Stage: UninstallStage.Verifying } => "Verifying...",
                _ => null,
            };

            if (line is not null)
            {
                writer.WriteLine(line);
            }
        }

        private static string Name(JobEvent e) => e.ImagePath is null ? string.Empty : Path.GetFileName(e.ImagePath);
    }
}
