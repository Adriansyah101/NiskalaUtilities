using System.Diagnostics;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Core.Models;

namespace Niskala.Cleaner;

public sealed record StartupHindrance(string Id, string Name, string Source, BootImpact Impact, string? Publisher);

public interface IStartupReview
{
    IReadOnlyList<StartupHindrance> Review(CancellationToken cancellationToken = default);
}

public sealed record QuickScanReport(
    IReadOnlyList<RulePreview> System,
    IReadOnlyList<RulePreview> Browsers,
    IReadOnlyList<StartupHindrance> Startup,
    TimeSpan Elapsed)
{
    public IReadOnlyList<RulePreview> Cleanable => [.. System.Concat(Browsers).Where(p => p.State == PreviewState.Ready)];

    public long SystemBytes => System.Where(p => p.State == PreviewState.Ready).Sum(p => p.Bytes);

    public long BrowserBytes => Browsers.Where(p => p.State == PreviewState.Ready).Sum(p => p.Bytes);

    public IReadOnlyList<string> LockedBrowsers => [.. Browsers.Where(p => p.LockedProfiles.Count > 0).Select(p => p.Rule.Browser!.Name).Distinct()];
}

public sealed class QuickScanner(CleanRuleCatalog catalog, CleanScanner scanner, IStartupReview? startup)
{
    public static readonly BootImpact HindranceThreshold = BootImpact.Medium;

    public QuickScanReport Scan(IProgress<RulePreview>? progress = null, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<CleanRule> quick = [.. catalog.Rules.Where(r => r.Quick && r.Action == RuleAction.Delete)];
        IReadOnlyList<RulePreview> previews = scanner.Preview(quick, progress, cancellationToken);
        IReadOnlyList<StartupHindrance> hindrances = startup is null
            ? []
            : [.. startup.Review(cancellationToken).Where(h => h.Impact >= HindranceThreshold).OrderByDescending(h => h.Impact).ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)];
        return new QuickScanReport(
            [.. previews.Where(p => p.Rule.Group == RuleGroup.System)],
            [.. previews.Where(p => p.Rule.Group == RuleGroup.Browser)],
            hindrances,
            Stopwatch.GetElapsedTime(start));
    }
}
