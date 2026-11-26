using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Cleaner;
using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Registry;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;

namespace Niskala.Cli.Commands;

internal sealed record CleanRuleRow(string Id, CleanCategory Category, string Name, string? Description, bool Quick, RemovalMode Removal, bool RequiresElevation, RuleAction Action, string? Browser);

internal sealed record CleanRulesOutput(int SchemaVersion, IReadOnlyList<CleanRuleRow> Rules, IReadOnlyList<RuleProblem> Problems);

internal sealed record CleanPreviewRow(string Id, string Name, PreviewState State, int Count, long Bytes, IReadOnlyList<string> LockedProfiles);

internal sealed record CleanRunOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    IReadOnlyList<CleanPreviewRow> Rules,
    IReadOnlyList<StartupHindrance> Startup,
    IReadOnlyList<RuleOutcome> Outcomes,
    TransactionRecord? Transaction);

internal sealed record IntegrityOutput(int SchemaVersion, DateTimeOffset GeneratedAt, bool DryRun, IReadOnlyList<IntegrityIssue> Issues, TransactionRecord? Transaction);

internal static class CleanCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose) =>
        new("clean", "Quick Scan, Selective Clean and Registry Integrity, journaled by Guardian; clean app removes an uninstalled app's leftovers.")
        {
            Quick(verbose), Rules(verbose), Run(verbose), Registry(verbose), CleanAppCommand.Create(verbose),
        };

    private static Command Quick(Option<bool> verbose)
    {
        var options = new CleanOptionSet();
        var command = new Command("quick", "Quick Scan: system junk, browser caches and heavy startup items in one pass, then clean the junk and caches.");
        options.AddTo(command);
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("clean quick", parse.GetValue(verbose), async (host, token) =>
        {
            CleanRuleCatalog catalog = host.LoadRuleCatalog();
            TextWriter o = options.Writer(parse);
            QuickScanReport report = host.CreateQuickScanner(catalog).Scan(cancellationToken: token);
            CleanerText.WriteQuickScan(o, report);
            return await CleanAsync(host, parse, options, report.Cleanable, report.Startup, "quick-scan", "Quick Scan", o, token).ConfigureAwait(false);
        }, cancellationToken));
        return command;
    }

    private static Command Rules(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write the rules as JSON to stdout." };
        var command = new Command("rules", "List the cleaning rules from the rules folder.") { json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("clean rules", parse.GetValue(verbose), (host, _) =>
        {
            CleanRuleCatalog catalog = host.LoadRuleCatalog();
            CleanRuleRow[] rows = [.. catalog.Rules.Select(r => new CleanRuleRow(
                r.Id, r.Category, r.DisplayName(false), r.Description?.En, r.Quick, r.Removal, r.RequiresElevation, r.Action, r.Browser?.Name))];
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new CleanRulesOutput(SchemaVersion, rows, catalog.Problems), CliJson.Output.CleanRulesOutput));
            }
            else
            {
                CleanerText.WriteRules(Console.Out, rows, catalog.Problems);
            }

            return Task.FromResult(catalog.Problems.Count == 0 ? ExitCodes.Success : ExitCodes.Failure);
        }, cancellationToken));
        return command;
    }

    private static Command Run(Option<bool> verbose)
    {
        var selectors = new Argument<string[]>("rules") { Description = "Rule ids, id prefixes ending in * (browser.firefox.*) or categories (crashDumps).", Arity = ArgumentArity.OneOrMore };
        var options = new CleanOptionSet();
        var command = new Command("run", "Selective Clean: preview and clean the chosen rules, with real byte counts.") { selectors };
        options.AddTo(command);
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("clean run", parse.GetValue(verbose), async (host, token) =>
        {
            CleanRuleCatalog catalog = host.LoadRuleCatalog();
            IReadOnlyList<CleanRule> rules = catalog.Select(parse.GetValue(selectors)!);
            if (rules.Count == 0)
            {
                await Console.Error.WriteLineAsync("No rule matches; niskala clean rules lists them.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            TextWriter o = options.Writer(parse);
            IReadOnlyList<RulePreview> previews = NiskalaHost.CreateCleanScanner().Preview(rules, cancellationToken: token);
            CleanerText.WritePreviews(o, previews);
            return await CleanAsync(host, parse, options, previews, [], "selective-clean", "Selective Clean", o, token).ConfigureAwait(false);
        }, cancellationToken));
        return command;
    }

    private static Command Registry(Option<bool> verbose)
    {
        var kinds = new Option<IntegrityIssueKind[]>("--check") { Description = "Run only these checks: FileAssociation, SharedDll, ComRegistration, FirewallRule, ServicePath.", AllowMultipleArgumentsPerToken = true };
        var fix = new Option<bool>("--fix") { Description = "Repair the issues found; a .reg backup and the journal are written first." };
        var dryRun = new Option<bool>("--dry-run") { Description = "With --fix, show what would be repaired; change nothing." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var noRestorePoint = new Option<bool>("--no-restore-point") { Description = "Skip the System Restore point; the .reg backup is still made." };
        var json = new Option<bool>("--json") { Description = "Write the issues and the transaction as JSON to stdout." };
        var command = new Command("registry", "Registry Integrity: find file types, shared files, COM classes, firewall rules and services that point at missing programs.")
        {
            kinds, fix, dryRun, yes, noRestorePoint, json,
        };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("clean registry", parse.GetValue(verbose), async (host, token) =>
        {
            bool asJson = parse.GetValue(json);
            bool isDryRun = parse.GetValue(dryRun) || !parse.GetValue(fix);
            TextWriter o = asJson ? Console.Error : Console.Out;
            RegistryIntegrityScanner scanner = host.CreateIntegrityScanner();
            IntegrityIssueKind[]? chosen = parse.GetValue(kinds) is { Length: > 0 } picked ? picked : null;
            IntegrityReport report = scanner.Scan(chosen, token);
            CleanerText.WriteIssues(o, report);
            IReadOnlyList<IntegrityIssue> fixable = report.Fixable;
            if (isDryRun || fixable.Count == 0)
            {
                o.WriteLine(fixable.Count == 0 ? "Nothing to repair." : parse.GetValue(fix) ? "Dry run: nothing was changed." : "Add --fix to repair them.");
                WriteJson(asJson, new IntegrityOutput(SchemaVersion, DateTimeOffset.Now, true, report.Issues, null));
                return ExitCodes.Success;
            }

            if (fixable.Any(i => i.MachineWide) && !Environment.IsPrivilegedProcess)
            {
                await Console.Error.WriteLineAsync("Machine-wide entries need an elevated prompt; nothing was changed.").ConfigureAwait(false);
                return ExitCodes.ElevationRequired;
            }

            if (await Confirmation.RequireAsync($"Repair {fixable.Count} registry entries?", parse.GetValue(yes), token).ConfigureAwait(false) is { } declined)
            {
                return declined;
            }

            IntegrityFix result = await host.CreateIntegrityFixer(scanner)
                .FixAsync(fixable, !parse.GetValue(noRestorePoint), new CleanText.Progress(Console.Error), token).ConfigureAwait(false);
            if (result.NoLongerBroken.Count > 0)
            {
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{result.NoLongerBroken.Count} entries healed since the scan and were left alone."));
            }

            CleanText.WriteTransaction(o, result.Result.Transaction);
            WriteJson(asJson, new IntegrityOutput(SchemaVersion, DateTimeOffset.Now, false, report.Issues, result.Result.Transaction));
            return CleanText.ExitCode(result.Result.Transaction);
        }, cancellationToken));
        return command;
    }

    private static async Task<int> CleanAsync(
        NiskalaHost host,
        ParseResult parse,
        CleanOptionSet options,
        IReadOnlyList<RulePreview> previews,
        IReadOnlyList<StartupHindrance> startup,
        string subjectId,
        string subjectName,
        TextWriter o,
        CancellationToken token)
    {
        bool asJson = parse.GetValue(options.Json);
        List<RulePreview> ready = [.. previews.Where(p => p.State == PreviewState.Ready)];
        CleanPreviewRow[] rows = [.. previews.Select(CleanerText.Row)];
        if (parse.GetValue(options.DryRun) || ready.Count == 0)
        {
            o.WriteLine(ready.Count == 0 ? "Nothing to clean." : "Dry run: nothing was changed.");
            WriteJson(asJson, new CleanRunOutput(SchemaVersion, DateTimeOffset.Now, true, rows, startup, [], null));
            return ExitCodes.Success;
        }

        string question = string.Create(CultureInfo.InvariantCulture, $"Clean {ready.Sum(p => p.Count)} items ({TextFormat.Bytes(ready.Sum(p => p.Bytes))})?");
        if (await Confirmation.RequireAsync(question, parse.GetValue(options.Yes), token).ConfigureAwait(false) is { } declined)
        {
            return declined;
        }

        var runOptions = new CleanRunOptions
        {
            SubjectId = subjectId,
            SubjectName = subjectName,
            Permanent = parse.GetValue(options.Permanent),
            CreateRestorePoint = !parse.GetValue(options.NoRestorePoint),
        };
        CleanRun run = await host.CreateSystemCleaner().CleanAsync(ready, runOptions, new CleanerText.Progress(Console.Error), token).ConfigureAwait(false);
        CleanerText.WriteRun(o, run);
        WriteJson(asJson, new CleanRunOutput(SchemaVersion, DateTimeOffset.Now, false, rows, startup, run.Rules, run.Transaction));
        return run.Rules.Any(r => r.Failed > 0) ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static void WriteJson(bool asJson, CleanRunOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.CleanRunOutput));
        }
    }

    private static void WriteJson(bool asJson, IntegrityOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.IntegrityOutput));
        }
    }

    private sealed class CleanOptionSet
    {
        public Option<bool> DryRun { get; } = new("--dry-run") { Description = "Show what would be cleaned; change nothing." };

        public Option<bool> Yes { get; } = new("--yes", "-y") { Description = "Do not ask for confirmation." };

        public Option<bool> Permanent { get; } = new("--permanent") { Description = "Delete traces permanently instead of moving them to the Recycle Bin." };

        public Option<bool> NoRestorePoint { get; } = new("--no-restore-point") { Description = "Skip the System Restore point; the journal is still written." };

        public Option<bool> Json { get; } = new("--json") { Description = "Write the plan and the transaction as JSON to stdout; progress goes to stderr." };

        public void AddTo(Command command)
        {
            command.Options.Add(DryRun);
            command.Options.Add(Yes);
            command.Options.Add(Permanent);
            command.Options.Add(NoRestorePoint);
            command.Options.Add(Json);
        }

        public TextWriter Writer(ParseResult parse) => parse.GetValue(Json) ? Console.Error : Console.Out;
    }
}
