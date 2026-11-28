using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class AnalysisCacheTests
{
    private static DriveAnalysis Analyze(FakeDisk disk) => new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);

    [Fact]
    public void An_analysis_is_kept_per_volume_serial_with_its_scan_time()
    {
        var clock = new StepClock();
        var cache = new VolumeAnalysisCache(clock);
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2), (20, 2));
        DriveAnalysis analysis = Analyze(disk);
        CachedAnalysis stored = cache.Store(analysis);
        Assert.Equal(StepClock.Start, stored.ScannedAt);
        Assert.True(cache.TryGet(disk.Volume with { FreeBytes = 1 }, out CachedAnalysis? found));
        Assert.Same(analysis, found.Analysis);
        Assert.Equal(stored.ScannedAt, found.ScannedAt);
        Assert.Same(analysis, cache.Find(disk.Volume));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Another_disk_on_the_same_letter_is_not_given_the_old_analysis()
    {
        var cache = new VolumeAnalysisCache();
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2));
        cache.Store(Analyze(disk));
        Assert.Null(cache.Find(disk.Volume with { SerialNumber = FakeDisk.InitialSerial + 1 }));
        Assert.Equal(0, cache.Count);
        cache.Store(Analyze(disk));
        Assert.Null(cache.Find(disk.Volume with { TotalClusters = 2_000 }));
        Assert.Null(cache.Find(disk.Volume with { FileSystem = "exFAT" }));
        Assert.Null(cache.Find(disk.Volume with { Letter = 'Y' }));
    }

    [Fact]
    public void Touching_after_a_defrag_keeps_the_scan_time_and_counts_the_update()
    {
        var clock = new StepClock();
        var cache = new VolumeAnalysisCache(clock);
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2));
        DriveAnalysis analysis = Analyze(disk);
        cache.Store(analysis);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(cache.Touch(analysis));
        Assert.True(cache.TryGet(disk.Volume, out CachedAnalysis? entry));
        Assert.Equal(StepClock.Start, entry.ScannedAt);
        Assert.Equal(StepClock.Start.AddMinutes(5), entry.UpdatedAt);
        Assert.Equal(1, entry.Updates);
        Assert.False(cache.Touch(Analyze(disk)));
        Assert.True(cache.Forget(disk.Volume));
        Assert.False(cache.Touch(analysis));
        Assert.Null(cache.Find(disk.Volume));
    }

    [Fact]
    public async Task The_optimizer_caches_each_analysis_and_updates_it_after_a_defrag()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2), (20, 2)).File("b.dat", (30, 2), (40, 2));
        var optimizer = new DriveOptimizer(disk);
        Assert.Null(optimizer.Cached(disk.Volume));
        DriveAnalysis analysis = await optimizer.AnalyzeAsync(disk.Volume, null, CancellationToken.None);
        Assert.Same(analysis, optimizer.Cached(disk.Volume));
        await optimizer.DefragmentAsync(analysis, DefragMode.Quick, [@"X:\a.dat"], null, CancellationToken.None);
        Assert.True(optimizer.Cache.TryGet(disk.Volume, out CachedAnalysis? entry));
        Assert.Same(analysis, entry.Analysis);
        Assert.Equal(1, entry.Updates);
        Assert.Equal(1, entry.Analysis.Summary().FragmentedFiles);
        DriveAnalysis again = await optimizer.AnalyzeAsync(disk.Volume, null, CancellationToken.None);
        Assert.NotSame(analysis, again);
        Assert.Same(again, optimizer.Cached(disk.Volume));
        Assert.Null(optimizer.Cached(disk.Reformat(FakeDisk.InitialSerial + 7).Volume));
    }

    [Fact]
    public void A_file_changed_after_the_analysis_is_read_again_before_it_is_moved()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2), (20, 2)).File("b.dat", (30, 2), (40, 2));
        DriveAnalysis analysis = Analyze(disk);
        disk.Relocate("a.dat", (500, 4));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 1, 0), (result.Moved, result.Defragmented, result.Failed));
        Assert.Equal([new Extent(0, 500, 4)], disk.ExtentsOf("a.dat"));
        DriveFile a = analysis.Files.Single(f => f.Name == "a.dat");
        Assert.Equal(1, a.Fragments);
        Assert.Equal(500, a.StartLcn);
        Assert.True(analysis.Bitmap.IsAllocated(500));
        Assert.False(analysis.Bitmap.IsAllocated(10));
        Assert.False(analysis.Free.IsFree(500, 1));
        Assert.All(disk.Moves, m => Assert.NotEqual(500, m.Lcn));
        Assert.Equal(analysis.Bitmap.Clusters - analysis.Bitmap.CountAllocated(0, analysis.Bitmap.Clusters), analysis.Free.TotalFree);
    }

    private sealed class StepClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

        private DateTimeOffset _now = Start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public sealed class FileBoundaryCancelTests
{
    private static DriveAnalysis Analyze(FakeDisk disk) => new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);

    [Fact]
    public void Cancel_finishes_the_file_being_moved_and_then_stops_the_queue()
    {
        FakeDisk disk = new FakeDisk(200_000)
            .File("big.vhdx", (0, 15_000), (20_000, 15_000), (40_000, 10_000))
            .File("next.dat", (60_000, 2), (60_010, 2));
        DriveAnalysis analysis = Analyze(disk);
        using var cancel = new CancellationTokenSource();
        disk.OnMove = moves =>
        {
            if (moves == 1)
            {
                cancel.Cancel();
            }
        };
        var phases = new List<DriveProgress>();
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, new Inline(phases.Add), cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal((1, 1), (result.Moved, result.Defragmented));
        Assert.Equal(3, disk.Moves.Count);
        Assert.Single(disk.ExtentsOf("big.vhdx"));
        Assert.Equal(1, analysis.Files.Single(f => f.Name == "big.vhdx").Fragments);
        Assert.Equal(2, disk.ExtentsOf("next.dat").Count);
        DriveProgress stopping = Assert.Single(phases, p => p.Phase == DrivePhase.Stopping);
        Assert.Equal(@"X:\big.vhdx", stopping.Current);
    }

    [Fact]
    public void No_file_ends_with_more_fragments_than_it_started_with_whenever_cancel_comes()
    {
        for (int stopAt = 1; stopAt <= 3; stopAt++)
        {
            FakeDisk disk = new FakeDisk(200_000).File("big.vhdx", (0, 15_000), (20_000, 15_000), (40_000, 10_000));
            DriveAnalysis analysis = Analyze(disk);
            using var cancel = new CancellationTokenSource();
            int at = stopAt;
            disk.OnMove = moves =>
            {
                if (moves == at)
                {
                    cancel.Cancel();
                }
            };
            new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, cancel.Token);
            Assert.True(DriveFile.CountFragments(disk.ExtentsOf("big.vhdx")) <= 3, $"cancel at move {at}");
            Assert.Single(disk.ExtentsOf("big.vhdx"));
        }
    }

    [Fact]
    public void A_file_too_large_to_finish_stops_at_the_next_chunk()
    {
        const long cluster = 64 << 10;
        FakeDisk disk = new FakeDisk(200_000, bytesPerCluster: cluster).File("huge.vhdx", (0, 20_000), (30_000, 20_000));
        DriveAnalysis analysis = Analyze(disk);
        DriveFile huge = analysis.Files.Single();
        Assert.False(DriveDefragmenter.FinishesOnCancel(huge, cluster));
        using var cancel = new CancellationTokenSource();
        disk.OnMove = moves =>
        {
            if (moves == 1)
            {
                cancel.Cancel();
            }
        };
        var phases = new List<DriveProgress>();
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, new Inline(phases.Add), cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Single(disk.Moves);
        Assert.DoesNotContain(phases, p => p.Phase == DrivePhase.Stopping);
    }

    [Fact]
    public void The_finish_limit_is_two_gigabytes()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2));
        DriveFile file = Analyze(disk).Files.Single();
        Assert.Equal(2L << 30, DriveDefragmenter.FinishLimitBytes);
        Assert.True(DriveDefragmenter.FinishesOnCancel(file, DriveDefragmenter.FinishLimitBytes / 2));
        Assert.False(DriveDefragmenter.FinishesOnCancel(file, (DriveDefragmenter.FinishLimitBytes / 2) + 1));
    }

    [Fact]
    public void A_cancel_that_comes_during_the_last_file_is_still_reported()
    {
        FakeDisk disk = new FakeDisk(200_000).File("big.vhdx", (0, 15_000), (20_000, 15_000), (40_000, 10_000));
        DriveAnalysis analysis = Analyze(disk);
        using var cancel = new CancellationTokenSource();
        disk.OnMove = moves =>
        {
            if (moves == 2)
            {
                cancel.Cancel();
            }
        };
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Defragmented);
    }

    private sealed class Inline(Action<DriveProgress> report) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => report(value);
    }
}
