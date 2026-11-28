namespace Niskala.Optimizer.Drives;

public enum StrategyGoal
{
    MaximumSpeed,
    LowImpact,
    ReclaimSpace,
}

public enum StrategyVerdict
{
    NeedsAnalysis,
    Recommended,
    AlreadyTidy,
    TrimOnly,
    AnalyzeOnly,
}

public enum StrategyReason
{
    NotAnalyzed,
    HeavyFragmentation,
    LightFragmentation,
    ScatteredFreeSpace,
    LowFreeSpace,
    AlreadyTidy,
    SolidState,
    UnknownMedia,
    UnsupportedFileSystem,
}

public enum StrategyFit
{
    Recommended,
    Suitable,
    Limited,
    NotApplicable,
}

public sealed record StrategyOption(StrategyGoal Goal, DefragMode Mode, StrategyFit Fit)
{
    public bool CanApply => Fit is StrategyFit.Recommended or StrategyFit.Suitable or StrategyFit.Limited;
}

public sealed record StrategyAdvice(StrategyVerdict Verdict, StrategyReason Reason, StrategyGoal? Recommended, IReadOnlyList<StrategyOption> Options, DriveAction Action)
{
    public bool UsesTrim => Action == DriveAction.Trim;

    public bool AllowsRelocation => Options.Any(o => o.CanApply);

    public StrategyOption Option(StrategyGoal goal) => Options.First(o => o.Goal == goal);
}

public static class StrategyGuide
{
    public const double HeavyFragmentedPercent = 10;
    public const double TidyFragmentedPercent = 2;
    public const double LowFreePercent = 15;
    public const double ScatteredFreeShare = 0.5;
    public const double TidyFreeShare = 0.8;

    private static readonly StrategyGoal[] Goals = [StrategyGoal.MaximumSpeed, StrategyGoal.LowImpact, StrategyGoal.ReclaimSpace];

    public static IReadOnlyList<StrategyGoal> AllGoals => Goals;

    public static DefragMode ModeFor(StrategyGoal goal) => goal switch
    {
        StrategyGoal.MaximumSpeed => DefragMode.FullZone,
        StrategyGoal.ReclaimSpace => DefragMode.Consolidate,
        _ => DefragMode.Quick,
    };

    public static StrategyGoal GoalFor(DefragMode mode) => mode switch
    {
        DefragMode.FullZone => StrategyGoal.MaximumSpeed,
        DefragMode.Consolidate => StrategyGoal.ReclaimSpace,
        _ => StrategyGoal.LowImpact,
    };

    public static double FreePercent(DriveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.TotalClusters <= 0 ? 0 : summary.FreeClusters * 100.0 / summary.TotalClusters;
    }

    public static double FreeShare(DriveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.FreeClusters <= 0 ? 1 : Math.Clamp(summary.LargestFreeClusters / (double)summary.FreeClusters, 0, 1);
    }

    public static StrategyAdvice Advise(DriveVolume volume, DriveSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.Media == MediaKind.Ssd)
        {
            return Closed(StrategyVerdict.TrimOnly, StrategyReason.SolidState, DriveAction.Trim);
        }

        if (volume.Media != MediaKind.Hdd)
        {
            return Closed(StrategyVerdict.AnalyzeOnly, StrategyReason.UnknownMedia, DriveAction.AnalyzeOnly);
        }

        if (!volume.SupportsMoves)
        {
            return Closed(StrategyVerdict.AnalyzeOnly, StrategyReason.UnsupportedFileSystem, DriveAction.AnalyzeOnly);
        }

        if (summary is null)
        {
            return new StrategyAdvice(StrategyVerdict.NeedsAnalysis, StrategyReason.NotAnalyzed, null, Fits(null, limitSpeed: false), DriveAction.Defragment);
        }

        double fragmented = summary.FragmentedPercent;
        double share = FreeShare(summary);
        bool low = FreePercent(summary) < LowFreePercent;
        if (fragmented < TidyFragmentedPercent && share >= TidyFreeShare)
        {
            return new StrategyAdvice(StrategyVerdict.AlreadyTidy, StrategyReason.AlreadyTidy, null, Fits(null, low), DriveAction.Defragment);
        }

        (StrategyGoal goal, StrategyReason reason) = low ? (StrategyGoal.LowImpact, StrategyReason.LowFreeSpace)
            : fragmented >= HeavyFragmentedPercent ? (StrategyGoal.MaximumSpeed, StrategyReason.HeavyFragmentation)
            : share < ScatteredFreeShare ? (StrategyGoal.ReclaimSpace, StrategyReason.ScatteredFreeSpace)
            : (StrategyGoal.LowImpact, StrategyReason.LightFragmentation);
        return new StrategyAdvice(StrategyVerdict.Recommended, reason, goal, Fits(goal, low), DriveAction.Defragment);
    }

    private static StrategyAdvice Closed(StrategyVerdict verdict, StrategyReason reason, DriveAction action) =>
        new(verdict, reason, null, [.. Goals.Select(g => new StrategyOption(g, ModeFor(g), StrategyFit.NotApplicable))], action);

    private static StrategyOption[] Fits(StrategyGoal? recommended, bool limitSpeed) =>
    [
        .. Goals.Select(g => new StrategyOption(
            g,
            ModeFor(g),
            g == recommended ? StrategyFit.Recommended
            : limitSpeed && g == StrategyGoal.MaximumSpeed ? StrategyFit.Limited
            : StrategyFit.Suitable)),
    ];
}
