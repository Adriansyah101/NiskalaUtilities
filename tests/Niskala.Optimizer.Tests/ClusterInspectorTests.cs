using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class ClusterInspectorTests
{
    private static readonly DateTime Settled = FakeDisk.Now.AddYears(-1);

    private static DriveAnalysis Analyze(FakeDisk disk) => new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);

    private static ClusterInspection Inspect(DriveAnalysis analysis, long start, long end, int limit = ClusterInspector.MaxRows) =>
        ClusterInspector.Inspect(analysis, start, end, limit, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(10_000L, 8192, 100)]
    [InlineData(10_000L, 8192, 8192)]
    [InlineData(10_000L, 8192, 9000)]
    [InlineData(777L, 8192, 64)]
    [InlineData(5L, 8192, 3)]
    [InlineData(1_000_000_000L, 8192, 1777)]
    public void Cells_cover_the_volume_without_gaps_and_find_their_clusters(long clusters, int segments, int cells)
    {
        var map = new DriveMap(clusters, segments);
        long expected = 0;
        for (int cell = 0; cell < cells; cell++)
        {
            (long start, long end) = map.CellRange(cell, cells);
            Assert.True(end > start, $"cell {cell} is empty");
            Assert.True(start <= expected, $"cell {cell} leaves a gap");
            expected = Math.Max(expected, end);
            int found = map.CellOf(start, cells);
            (long foundStart, long foundEnd) = map.CellRange(found, cells);
            Assert.InRange(start, foundStart, foundEnd - 1);
            if (cells <= map.Segments)
            {
                Assert.Equal(cell, found);
                Assert.Equal(cell, map.CellOf(end - 1, cells));
            }
        }

        Assert.Equal(clusters, expected);
        Assert.Equal(0, map.CellRange(0, cells).Start);
    }

    [Fact]
    public void Cell_lookups_clamp_outside_clusters_and_reject_bad_cells()
    {
        var map = new DriveMap(1_000, 100);
        Assert.Equal(0, map.CellOf(-5, 10));
        Assert.Equal(9, map.CellOf(5_000, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.CellRange(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.CellRange(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.CellRange(0, 0));
    }

    [Fact]
    public void A_block_lists_the_files_that_occupy_it_with_their_state()
    {
        FakeDisk disk = new FakeDisk(1_000)
            .File("whole.txt", (100, 40))
            .File("split.dat", (140, 5), (600, 5))
            .Folder("Media")
            .File(@"Media\film.mkv", (150, 30))
            .File("outside.txt", (700, 10))
            .Reserve(185, 5);
        DriveAnalysis analysis = Analyze(disk);

        ClusterInspection inspection = Inspect(analysis, 100, 200);

        Assert.Equal((100L, 200L, 100L), (inspection.Start, inspection.End, inspection.Clusters));
        Assert.Equal(3, inspection.TotalFiles);
        Assert.Equal(["whole.txt", "film.mkv", "split.dat"], inspection.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal([40L, 30L, 5L], inspection.Files.Select(f => f.ClustersInRange));
        Assert.Equal([OccupantState.Contiguous, OccupantState.Archive, OccupantState.Fragmented], inspection.Files.Select(f => f.State));
        Assert.Equal([100L, 150L, 140L], inspection.Files.Select(f => f.FirstLcn));
        Assert.Equal(2, inspection.Files.Single(f => f.State == OccupantState.Fragmented).Fragments);
        Assert.Equal(5, inspection.UnlistedClusters);
        Assert.Equal(100 - 40 - 30 - 5 - 5, inspection.FreeClusters);
        Assert.Equal(80, inspection.UsedClusters);
        Assert.DoesNotContain(inspection.Files, f => f.Path.EndsWith("outside.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_in_use_show_as_system_or_locked()
    {
        FakeDisk disk = new FakeDisk(1_000).File("open.dat", (10, 2), (20, 2)).File("other.dat", (40, 4));
        disk.Locked.Add(@"X:\open.dat");
        DriveAnalysis analysis = Analyze(disk);
        new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);

        ClusterInspection inspection = Inspect(analysis, 0, 50);

        Assert.Equal(OccupantState.SystemLocked, inspection.Files.Single(f => f.Path.EndsWith("open.dat", StringComparison.Ordinal)).State);
        Assert.Equal(OccupantState.Contiguous, inspection.Files.Single(f => f.Path.EndsWith("other.dat", StringComparison.Ordinal)).State);
    }

    [Fact]
    public void An_empty_block_is_all_free_and_a_reversed_range_is_clamped()
    {
        DriveAnalysis analysis = Analyze(new FakeDisk(1_000).File("a.txt", (0, 10)));

        ClusterInspection free = Inspect(analysis, 500, 600);
        ClusterInspection clamped = Inspect(analysis, 900, 5_000);
        ClusterInspection reversed = Inspect(analysis, 300, 200);

        Assert.True(free.IsEmpty);
        Assert.Equal(100, free.FreeClusters);
        Assert.Equal((900L, 1_000L), (clamped.Start, clamped.End));
        Assert.Equal(0, reversed.Clusters);
        Assert.Empty(reversed.Files);
    }

    [Fact]
    public void Large_blocks_keep_the_biggest_rows_and_count_the_rest()
    {
        var disk = new FakeDisk(2_000);
        for (int i = 0; i < 30; i++)
        {
            disk.File($"f{i:00}.txt", (i * 50, 1 + i));
        }

        ClusterInspection inspection = Inspect(Analyze(disk), 0, 2_000, limit: 10);

        Assert.Equal(30, inspection.TotalFiles);
        Assert.Equal(10, inspection.Files.Count);
        Assert.Equal("f29.txt", Path.GetFileName(inspection.Files[0].Path));
        Assert.True(inspection.Files.Zip(inspection.Files.Skip(1)).All(p => p.First.ClustersInRange >= p.Second.ClustersInRange));
    }

    [Fact]
    public void Settled_large_files_are_reported_as_archive()
    {
        FakeDisk disk = new FakeDisk(300_000).File("vm.dat", Settled, (0, (ZoneClassifier.LargeFileBytes / 4096) + 1));
        ClusterInspection inspection = Inspect(Analyze(disk), 0, 10);

        Assert.Equal(OccupantState.Archive, Assert.Single(inspection.Files).State);
    }

    [Fact]
    public void A_cancelled_inspection_stops()
    {
        var disk = new FakeDisk(20_000);
        for (int i = 0; i < ClusterInspector.CheckEvery + 10; i++)
        {
            disk.File($"n{i}.txt", (i, 1));
        }

        DriveAnalysis analysis = Analyze(disk);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => ClusterInspector.Inspect(analysis, 0, 20_000, cancellationToken: cancel.Token));
    }
}
