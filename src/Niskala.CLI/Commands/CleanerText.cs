using System.Globalization;
using Niskala.Cleaner;
using Niskala.Cleaner.Databases;
using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Registry;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;

namespace Niskala.Cli.Commands;

internal static class CleanerText
{
    public static CleanPreviewRow Row(RulePreview preview) =>
        new(preview.Rule.Id, preview.Rule.DisplayName(false), preview.State, preview.Count, preview.Bytes, preview.LockedProfiles);

    public static void WriteRules(TextWriter o, IReadOnlyList<CleanRuleRow> rules, IReadOnlyList<RuleProblem> problems)
    {
        foreach (CleanRuleRow rule in rules)
        {
            string flags = string.Join(", ", new[]
            {
                rule.Quick ? "quick scan" : null,
                rule.Action == RuleAction.Compact ? "compacts" : rule.Removal == RemovalMode.RecycleBin ? "Recycle Bin" : "permanent",
                rule.RequiresElevation ? "needs elevation" : null,
            }.OfType<string>());
            o.WriteLine($"{TextFormat.Pad(rule.Id, 34)} {TextFormat.Pad(rule.Name, 44)} {flags}");
        }

        foreach (RuleProblem problem in problems)
        {
            o.WriteLine($"Rules file {problem.Source}: {problem.Message}");
        }
    }

    public static void WritePreviews(TextWriter o, IReadOnlyList<RulePreview> previews)
    {
        foreach (RulePreview preview in previews)
        {
            string state = preview.State switch
            {
                PreviewState.Ready => string.Create(CultureInfo.InvariantCulture, $"{preview.Count} {(preview.Rule.Action == RuleAction.Compact ? "databases" : "files")}, {TextFormat.Bytes(preview.Bytes)}"),
                PreviewState.NeedsElevation => "needs an elevated prompt",
                PreviewState.Locked => $"skipped: open in {string.Join(", ", preview.LockedProfiles)}",
                _ => "nothing to clean",
            };
            o.WriteLine($"  {TextFormat.Pad(preview.Rule.DisplayName(false), 52)} {state}");
        }
    }

    public static void WriteQuickScan(TextWriter o, QuickScanReport report)
    {
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"System junk: {TextFormat.Bytes(report.SystemBytes)}"));
        WritePreviews(o, report.System);
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Browser caches: {TextFormat.Bytes(report.BrowserBytes)}"));
        WritePreviews(o, report.Browsers);
        if (report.LockedBrowsers.Count > 0)
        {
            o.WriteLine($"  Close {string.Join(", ", report.LockedBrowsers)} and scan again to include them.");
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Startup items with a medium or high boot impact: {report.Startup.Count}"));
        foreach (StartupHindrance item in report.Startup)
        {
            o.WriteLine($"  {item.Impact,-7} {TextFormat.Pad(item.Name, 40)} {item.Source}  niskala optimize disable \"{item.Id}\"");
        }
    }

    public static void WriteIssues(TextWriter o, IntegrityReport report)
    {
        foreach (IGrouping<IntegrityIssueKind, IntegrityIssue> group in report.Issues.GroupBy(i => i.Kind))
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{group.Key} ({group.Count()})"));
            foreach (IntegrityIssue issue in group)
            {
                string place = issue.ValueName is { Length: > 0 } value ? $"{issue.Path} -> {value}" : issue.Path;
                o.WriteLine($"  {place}");
                o.WriteLine($"    {issue.Detail}{(issue.Fixable ? string.Empty : $"  [left alone: {issue.Protection}]")}");
            }
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{report.Issues.Count} issues, {report.Fixable.Count} repairable, in {report.Elapsed.TotalSeconds:0.0} s."));
    }

    public static void WriteRun(TextWriter o, CleanRun run)
    {
        o.WriteLine();
        if (run.Transaction.RestorePoint is { } point)
        {
            o.WriteLine(point.Created
                ? string.Create(CultureInfo.InvariantCulture, $"Restore point #{point.SequenceNumber} created.")
                : $"Restore point: {point.Note}");
        }

        foreach (RuleOutcome rule in run.Rules)
        {
            string text = rule switch
            {
                { Skipped: { } reason } => "skipped: " + reason,
                { Compactions.Count: > 0 } => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{rule.Compactions.Count(c => c.Outcome == CompactionOutcome.Compacted)} of {rule.Compactions.Count} databases compacted, {TextFormat.Bytes(rule.Bytes)} saved"),
                _ => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{rule.Removed + rule.Recycled} files, {TextFormat.Bytes(rule.Bytes)}{Extra(rule)}"),
            };
            o.WriteLine($"  {TextFormat.Pad(rule.RuleId, 34)} {text}");
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Freed {TextFormat.Bytes(run.Bytes)}. Transaction {run.Transaction.Id}: niskala restore {run.Transaction.Id} brings back what went to the Recycle Bin."));
    }

    private static string Extra(RuleOutcome rule)
    {
        var parts = new List<string>();
        Add(rule.Recycled, "to the Recycle Bin");
        Add(rule.InUse, "in use");
        Add(rule.Refused, "protected");
        Add(rule.Changed, "changed since the scan");
        Add(rule.Failed, "failed");
        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";

        void Add(int count, string label)
        {
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {label}"));
            }
        }
    }

    internal sealed class Progress(TextWriter writer) : IProgress<CleanRunProgress>
    {
        private string? _rule;

        public void Report(CleanRunProgress value)
        {
            if (value.RuleId != _rule)
            {
                _rule = value.RuleId;
                writer.WriteLine($"Cleaning {value.RuleId}…");
            }
        }
    }
}
