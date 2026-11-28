using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class StrategyGuideTests
{
    private const long Clusters = 1_000_000;

    private static DriveVolume Volume(MediaKind media, string fileSystem = "NTFS", bool trim = true) =>
        new('D', "Data", fileSystem, 4096, Clusters, Clusters * 4096, Clusters * 2048, media, trim);

    private static DriveSummary Summary(double fragmentedPercent, long freeClusters, long largestFree) =>
        new(Clusters, freeClusters, 10_000, 100, 400, fragmentedPercent, largestFree, 100, 9_800, 100, 2, 0, 0);

    [Fact]
    public void Goals_map_to_the_matching_defrag_modes_both_ways()
    {
        Assert.Equal(DefragMode.FullZone, StrategyGuide.ModeFor(StrategyGoal.MaximumSpeed));
        Assert.Equal(DefragMode.Quick, StrategyGuide.ModeFor(StrategyGoal.LowImpact));
        Assert.Equal(DefragMode.Consolidate, StrategyGuide.ModeFor(StrategyGoal.ReclaimSpace));
        Assert.All(StrategyGuide.AllGoals, g => Assert.Equal(g, StrategyGuide.GoalFor(StrategyGuide.ModeFor(g))));
        Assert.Equal(3, StrategyGuide.AllGoals.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Solid_state_drives_get_trim_only_and_no_relocation_goal(bool analyzed)
    {
        StrategyAdvice advice = StrategyGuide.Advise(Volume(MediaKind.Ssd), analyzed ? Summary(40, 100_000, 1_000) : null);

        Assert.Equal(StrategyVerdict.TrimOnly, advice.Verdict);
        Assert.Equal(StrategyReason.SolidState, advice.Reason);
        Assert.True(advice.UsesTrim);
        Assert.Null(advice.Recommended);
        Assert.False(advice.AllowsRelocation);
        Assert.Equal(3, advice.Options.Count);
        Assert.All(advice.Options, o =>
        {
            Assert.Equal(StrategyFit.NotApplicable, o.Fit);
            Assert.False(o.CanApply);
        });
    }

    [Fact]
    public void Unknown_media_and_fixed_file_systems_are_only_analyzed()
    {
        StrategyAdvice unknown = StrategyGuide.Advise(Volume(MediaKind.Unknown), Summary(30, 500_000, 400_000));
        StrategyAdvice refs = StrategyGuide.Advise(Volume(MediaKind.Hdd, "ReFS"), Summary(30, 500_000, 400_000));

        Assert.Equal((StrategyVerdict.AnalyzeOnly, StrategyReason.UnknownMedia, DriveAction.AnalyzeOnly), (unknown.Verdict, unknown.Reason, unknown.Action));
        Assert.Equal((StrategyVerdict.AnalyzeOnly, StrategyReason.UnsupportedFileSystem), (refs.Verdict, refs.Reason));
        Assert.False(unknown.AllowsRelocation);
        Assert.False(refs.AllowsRelocation);
    }

    [Fact]
    public void An_unanalyzed_hard_disk_asks_for_an_analysis_but_offers_every_goal()
    {
        StrategyAdvice advice = StrategyGuide.Advise(Volume(MediaKind.Hdd), null);

        Assert.Equal((StrategyVerdict.NeedsAnalysis, StrategyReason.NotAnalyzed), (advice.Verdict, advice.Reason));
        Assert.Null(advice.Recommended);
        Assert.All(advice.Options, o => Assert.Equal(StrategyFit.Suitable, o.Fit));
        Assert.False(advice.UsesTrim);
    }

    [Theory]
    [InlineData(25, 500_000, 450_000, StrategyGoal.MaximumSpeed, StrategyReason.HeavyFragmentation)]
    [InlineData(StrategyGuide.HeavyFragmentedPercent, 500_000, 450_000, StrategyGoal.MaximumSpeed, StrategyReason.HeavyFragmentation)]
    [InlineData(5, 500_000, 450_000, StrategyGoal.LowImpact, StrategyReason.LightFragmentation)]
    [InlineData(5, 500_000, 100_000, StrategyGoal.ReclaimSpace, StrategyReason.ScatteredFreeSpace)]
    [InlineData(1, 500_000, 100_000, StrategyGoal.ReclaimSpace, StrategyReason.ScatteredFreeSpace)]
    [InlineData(25, 100_000, 90_000, StrategyGoal.LowImpact, StrategyReason.LowFreeSpace)]
    public void Hard_disks_get_the_goal_their_state_calls_for(double fragmented, long free, long largest, StrategyGoal goal, StrategyReason reason)
    {
        StrategyAdvice advice = StrategyGuide.Advise(Volume(MediaKind.Hdd), Summary(fragmented, free, largest));

        Assert.Equal(StrategyVerdict.Recommended, advice.Verdict);
        Assert.Equal(goal, advice.Recommended);
        Assert.Equal(reason, advice.Reason);
        Assert.Equal(StrategyFit.Recommended, advice.Option(goal).Fit);
        Assert.Single(advice.Options, o => o.Fit == StrategyFit.Recommended);
        Assert.All(advice.Options, o => Assert.True(o.CanApply));
        Assert.Equal(StrategyGuide.ModeFor(goal), advice.Option(goal).Mode);
    }

    [Fact]
    public void Little_free_space_marks_maximum_speed_as_limited()
    {
        StrategyAdvice advice = StrategyGuide.Advise(Volume(MediaKind.Hdd), Summary(25, 50_000, 40_000));

        Assert.Equal(StrategyFit.Limited, advice.Option(StrategyGoal.MaximumSpeed).Fit);
        Assert.Equal(StrategyFit.Recommended, advice.Option(StrategyGoal.LowImpact).Fit);
        Assert.Equal(StrategyFit.Suitable, advice.Option(StrategyGoal.ReclaimSpace).Fit);
    }

    [Fact]
    public void A_tidy_hard_disk_needs_nothing()
    {
        StrategyAdvice advice = StrategyGuide.Advise(Volume(MediaKind.Hdd), Summary(0.5, 500_000, 490_000));

        Assert.Equal((StrategyVerdict.AlreadyTidy, StrategyReason.AlreadyTidy), (advice.Verdict, advice.Reason));
        Assert.Null(advice.Recommended);
        Assert.DoesNotContain(advice.Options, o => o.Fit == StrategyFit.Recommended);
    }

    [Fact]
    public void Free_share_and_percent_handle_full_and_empty_drives()
    {
        Assert.Equal(1, StrategyGuide.FreeShare(Summary(0, 0, 0)));
        Assert.Equal(0.25, StrategyGuide.FreeShare(Summary(0, 400_000, 100_000)));
        Assert.Equal(40, StrategyGuide.FreePercent(Summary(0, 400_000, 100_000)));
        Assert.Equal(0, StrategyGuide.FreePercent(new DriveSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void The_advice_follows_a_real_analysis_of_a_scattered_disk()
    {
        var disk = new FakeDisk(10_000);
        for (int i = 0; i < 40; i++)
        {
            disk.File($"part{i}.dat", (i * 250, 20));
        }

        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        StrategyAdvice advice = StrategyGuide.Advise(analysis.Volume, analysis.Summary());

        Assert.Equal(StrategyGoal.ReclaimSpace, advice.Recommended);
        Assert.Equal(DefragMode.Consolidate, advice.Option(StrategyGoal.ReclaimSpace).Mode);
    }
}
