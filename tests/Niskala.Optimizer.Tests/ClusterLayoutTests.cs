using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class ClusterLayoutTests
{
    private static DriveAnalysis Analyze(FakeDisk disk) => new DriveAnalyzer(disk).Analyze(disk.Volume, null, TestContext.Current.CancellationToken);

    private static ClusterInspection Inspect(DriveAnalysis analysis, long start, long end) =>
        ClusterInspector.Inspect(analysis, start, end, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void Reserved_clusters_win_over_free_space_and_unowned_system_clusters()
    {
        Assert.Equal(ClusterState.MftReserved, DriveMap.Pick(total: 100, allocated: 0, owned: 0, fragmented: 0, archive: 0, reserved: 100));
        Assert.Equal(ClusterState.MftReserved, DriveMap.Pick(100, 100, 0, 0, 0, reserved: 100));
        Assert.Equal(ClusterState.Free, DriveMap.Pick(100, 0, 0, 0, 0, reserved: 20));
        Assert.Equal(ClusterState.Used, DriveMap.Pick(100, 100, 100, 0, 0, reserved: 100));
        Assert.Equal(ClusterState.Compressed, DriveMap.Pick(100, 90, 90, 0, 0, compressed: 60));
        Assert.Equal(ClusterState.Used, DriveMap.Pick(100, 90, 90, 0, 0, compressed: 20));
        Assert.Equal(ClusterState.Free, DriveMap.Pick(100, 0, 0, 0, 0));
    }

    [Fact]
    public void The_map_paints_the_reserved_zone_and_compressed_files_in_their_own_colors()
    {
        var disk = new FakeDisk(1_000) { DirectRecords = true }
            .Metadata(0, 60)
            .File("plain.dat", (300, 100))
            .File("packed.dat", (600, 100))
            .Compress("packed.dat");
        disk.ReservedZone.Add(new FreeRun(100, 100));

        DriveAnalysis analysis = Analyze(disk);
        ClusterState[] cells = analysis.Map.Summarize(10);

        Assert.Equal(ClusterState.MftReserved, cells[0]);
        Assert.Equal(ClusterState.MftReserved, cells[1]);
        Assert.Equal(ClusterState.Free, cells[2]);
        Assert.Equal(ClusterState.Used, cells[3]);
        Assert.Equal(ClusterState.Compressed, cells[6]);
        Assert.Equal([new FreeRun(0, 60), new FreeRun(100, 100)], analysis.Reserved);
        Assert.Equal((160L, 100L), analysis.Map.SpecialTotals());
        Assert.True(analysis.Files.Single(f => f.Name == "packed.dat").IsCompressed);
        Assert.Equal(ClusterState.Compressed, analysis.Files.Single(f => f.Name == "packed.dat").State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Compressed_files_are_recognised_by_every_scan_method(bool table, bool records)
    {
        var disk = new FakeDisk(1_000) { FileTable = table, DirectRecords = records }
            .Folder("Docs")
            .File(@"Docs\packed.log", (10, 5), (40, 5))
            .File(@"Docs\plain.log", (80, 5))
            .Compress(@"Docs\packed.log");

        DriveAnalysis analysis = Analyze(disk);

        Assert.True(analysis.Files.Single(f => f.Name == "packed.log").IsCompressed);
        Assert.False(analysis.Files.Single(f => f.Name == "plain.log").IsCompressed);
        Assert.Equal(1, analysis.Summary().CompressedFiles);
    }

    [Fact]
    public void Only_the_file_record_scan_sees_metadata_extents_and_the_layout_adds_the_zone()
    {
        var disk = new FakeDisk(1_000) { FileTable = true }.Metadata(0, 40).File("a.dat", (500, 10));
        disk.ReservedZone.Add(new FreeRun(40, 60));

        DriveAnalysis analysis = Analyze(disk);

        Assert.Equal([new FreeRun(40, 60)], analysis.Reserved);
        Assert.Equal(60, analysis.Summary().ReservedClusters);
    }

    [Fact]
    public void Free_space_fragments_count_every_separate_gap()
    {
        var disk = new FakeDisk(1_000).File("a.dat", (100, 50)).File("b.dat", (300, 50)).File("c.dat", (900, 100));

        DriveSummary summary = Analyze(disk).Summary();

        Assert.Equal(3, summary.FreeFragments);
        Assert.Equal(800, summary.FreeClusters);
        Assert.Equal(550, summary.LargestFreeClusters);
    }

    [Fact]
    public void A_completely_full_or_empty_drive_has_zero_or_one_free_fragment()
    {
        Assert.Equal(1, Analyze(new FakeDisk(100).File("x.dat", (0, 10))).Summary().FreeFragments);
        Assert.Equal(0, Analyze(new FakeDisk(100).File("x.dat", (0, 100))).Summary().FreeFragments);
    }

    [Fact]
    public void The_inspector_names_compressed_files_and_the_reserved_share_of_a_block()
    {
        var disk = new FakeDisk(1_000) { DirectRecords = true }.Metadata(0, 20).File("packed.dat", (20, 30)).Compress("packed.dat");
        disk.ReservedZone.Add(new FreeRun(50, 50));
        DriveAnalysis analysis = Analyze(disk);

        ClusterInspection inspection = Inspect(analysis, 0, 100);

        ClusterOccupant packed = Assert.Single(inspection.Files);
        Assert.Equal(OccupantState.Compressed, packed.State);
        Assert.Equal(70, inspection.ReservedClusters);
        Assert.Equal(50, inspection.FreeClusters);
        Assert.Equal(20, inspection.UnlistedClusters);
    }

    [Fact]
    public void Reserved_ranges_merge_overlaps_and_clip_to_the_volume()
    {
        IReadOnlyList<FreeRun> merged = ReservedArea.Merge([new FreeRun(50, 20), new FreeRun(0, 10), new FreeRun(60, 30), new FreeRun(950, 100), new FreeRun(5, 0)], 1_000);

        Assert.Equal([new FreeRun(0, 10), new FreeRun(50, 40), new FreeRun(950, 50)], merged);
        Assert.Equal(10, ReservedArea.Overlap(merged, 5, 55));
        Assert.Equal(0, ReservedArea.Overlap(merged, 100, 900));
        Assert.Empty(ReservedArea.Merge([], 1_000));
    }

    [Theory]
    [InlineData(0, DriveAssessment.Tidy)]
    [InlineData(1.9, DriveAssessment.Tidy)]
    [InlineData(StrategyGuide.TidyFragmentedPercent, DriveAssessment.SlightlyFragmented)]
    [InlineData(9.9, DriveAssessment.SlightlyFragmented)]
    [InlineData(StrategyGuide.HeavyFragmentedPercent, DriveAssessment.HeavilyFragmented)]
    [InlineData(60, DriveAssessment.HeavilyFragmented)]
    public void The_assessment_follows_the_fragmented_share(double percent, DriveAssessment expected)
    {
        var summary = new DriveSummary(1_000, 500, 10, 1, 1, percent, 400, 0, 10, 0, 0, 0, 0);
        Assert.Equal(expected, DriveAssessor.Assess(summary));
    }

    [Fact]
    public void Unflagged_files_keep_their_usual_states()
    {
        DriveAnalysis analysis = Analyze(new FakeDisk(1_000).File("split.dat", (10, 5), (500, 5)).File("whole.dat", (100, 5)));

        Assert.Equal(ClusterState.Fragmented, analysis.Files.Single(f => f.Name == "split.dat").State);
        Assert.Equal(ClusterState.Used, analysis.Files.Single(f => f.Name == "whole.dat").State);
        Assert.Empty(analysis.Reserved);
        Assert.Equal((0L, 0L), analysis.Map.SpecialTotals());
    }
}
