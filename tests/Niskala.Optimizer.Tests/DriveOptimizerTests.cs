using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class ProtectedFileTests
{
    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("$MFT")]
    [InlineData("$LogFile")]
    [InlineData("$Volume")]
    [InlineData("$Bitmap")]
    [InlineData(@"$Extend\$UsnJrnl")]
    [InlineData(@"System Volume Information\tracking.log")]
    [InlineData("PAGEFILE.SYS")]
    [InlineData(@"Backup\Recovered\Lost Partition 6(NTFS)\System Volume Information\tracking.log")]
    [InlineData(@"Backup\system volume information")]
    public void Kernel_owned_files_are_left_out(string relative) => Assert.True(ProtectedFiles.IsProtected(relative));

    [Theory]
    [InlineData(@"Users\pagefile.sys")]
    [InlineData(@"Windows\System32\kernel32.dll")]
    [InlineData("pagefile.sys.bak")]
    [InlineData("data.iso")]
    [InlineData(@"Notes\System Volume Information.txt")]
    [InlineData(@"Backup\$MFT")]
    public void Ordinary_files_are_scanned(string relative) => Assert.False(ProtectedFiles.IsProtected(relative));
}

public sealed class ZoneClassifierTests
{
    private static readonly DateTime Now = FakeDisk.Now;

    [Theory]
    [InlineData(@"C:\Windows\System32\ntdll.dll", SpeedZone.Fast)]
    [InlineData(@"C:\Program Files\App\app.EXE", SpeedZone.Fast)]
    [InlineData(@"C:\Windows\System32\drivers\disk.sys", SpeedZone.Fast)]
    [InlineData(@"D:\Images\win.iso", SpeedZone.Archive)]
    [InlineData(@"D:\Backups\old.zip", SpeedZone.Archive)]
    [InlineData(@"D:\Backups\old.7z", SpeedZone.Archive)]
    [InlineData(@"D:\Backups\old.rar", SpeedZone.Archive)]
    [InlineData(@"D:\Video\film.mkv", SpeedZone.Archive)]
    [InlineData(@"D:\Video\clip.mp4", SpeedZone.Archive)]
    [InlineData(@"D:\Work\db.bak", SpeedZone.Archive)]
    [InlineData(@"D:\Work\report.docx", SpeedZone.Standard)]
    [InlineData(@"D:\Work\notes", SpeedZone.Standard)]
    public void Extensions_pick_the_zone(string path, SpeedZone zone) => Assert.Equal(zone, ZoneClassifier.Classify(path, 1024, Now, Now));

    [Fact]
    public void Large_settled_files_go_to_the_archive_zone()
    {
        Assert.Equal(SpeedZone.Archive, ZoneClassifier.Classify(@"D:\vm\disk.dat", ZoneClassifier.LargeFileBytes, Now - ZoneClassifier.SettledAge, Now));
        Assert.Equal(SpeedZone.Standard, ZoneClassifier.Classify(@"D:\vm\disk.dat", ZoneClassifier.LargeFileBytes, Now.AddDays(-1), Now));
        Assert.Equal(SpeedZone.Standard, ZoneClassifier.Classify(@"D:\vm\disk.dat", ZoneClassifier.LargeFileBytes - 1, Now.AddYears(-3), Now));
        Assert.Equal(SpeedZone.Fast, ZoneClassifier.Classify(@"D:\vm\huge.dll", ZoneClassifier.LargeFileBytes, Now.AddYears(-3), Now));
    }

    [Fact]
    public void Layout_puts_fast_first_and_archive_last()
    {
        ZoneLayout layout = ZoneLayout.For(10_000, fast: 1_000, archive: 2_000);
        Assert.Equal((0L, layout.FastEnd), layout.Range(SpeedZone.Fast));
        Assert.Equal((layout.ArchiveStart, 10_000L), layout.Range(SpeedZone.Archive));
        Assert.True(layout.FastEnd >= 1_000);
        Assert.True(10_000 - layout.ArchiveStart >= 2_000);
        Assert.True(layout.FastEnd <= layout.ArchiveStart);
        Assert.Equal(SpeedZone.Fast, layout.ZoneOf(0));
        Assert.Equal(SpeedZone.Standard, layout.ZoneOf(layout.FastEnd));
        Assert.Equal(SpeedZone.Archive, layout.ZoneOf(9_999));
        Assert.True(layout.Holds(SpeedZone.Archive, 9_000, 1_000));
        Assert.False(layout.Holds(SpeedZone.Archive, 9_000, 1_001));
    }

    [Fact]
    public void Layout_never_crosses_on_a_full_volume()
    {
        ZoneLayout layout = ZoneLayout.For(1_000, fast: 900, archive: 900);
        Assert.True(layout.FastEnd <= layout.ArchiveStart);
        Assert.True(layout.ArchiveStart <= 1_000);
    }
}

public sealed class ClusterStructureTests
{
    [Fact]
    public void Bitmap_counts_and_lists_free_runs_across_words()
    {
        var bitmap = new ClusterBitmap(200);
        bitmap.Set(0, 10, allocated: true);
        bitmap.Set(60, 10, allocated: true);
        bitmap.Set(190, 10, allocated: true);
        Assert.Equal(30, bitmap.CountAllocated(0, 200));
        Assert.Equal(5, bitmap.CountAllocated(65, 10));
        Assert.True(bitmap.IsFree(10, 50));
        Assert.False(bitmap.IsFree(10, 51));
        Assert.Equal([new FreeRun(10, 50), new FreeRun(70, 120)], bitmap.FreeRuns());
    }

    [Fact]
    public void Bitmap_reads_the_volume_byte_order()
    {
        ClusterBitmap bitmap = ClusterBitmap.FromBytes([0b0000_0101, 0xFF, 0x80], 20);
        Assert.True(bitmap.IsAllocated(0));
        Assert.False(bitmap.IsAllocated(1));
        Assert.True(bitmap.IsAllocated(2));
        Assert.True(bitmap.IsAllocated(8));
        Assert.True(bitmap.IsAllocated(15));
        Assert.False(bitmap.IsAllocated(19));
        Assert.Equal(10, bitmap.CountAllocated(0, 20));
    }

    [Fact]
    public void Bitmap_words_are_used_in_place_with_the_tail_cleared()
    {
        ulong[] words = [0b101UL, ulong.MaxValue];
        ClusterBitmap bitmap = ClusterBitmap.FromWords(words, 70);
        Assert.Equal(2 + 6, bitmap.CountAllocated(0, 70));
        Assert.Equal(0x3FUL, words[1]);
        bitmap.Set(1, 1, allocated: true);
        Assert.Equal(0b111UL, words[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterBitmap.FromWords([0UL], 65));
    }

    [Fact]
    public void Best_fit_takes_the_smallest_run_that_holds_the_file()
    {
        var index = new FreeSpaceIndex([new FreeRun(0, 100), new FreeRun(200, 12), new FreeRun(300, 10), new FreeRun(400, 50)]);
        Assert.Equal(300, index.BestFit(10));
        Assert.Equal(200, index.BestFit(11));
        Assert.Equal(400, index.BestFit(13));
        Assert.Equal(0, index.BestFit(51));
        Assert.Null(index.BestFit(101));
        Assert.Equal(400, index.BestFit(20, from: 250));
        Assert.Equal(410, index.BestFit(20, from: 410));
        Assert.Null(index.BestFit(20, from: 250, to: 410));
    }

    [Fact]
    public void First_fit_takes_the_lowest_run_inside_the_range()
    {
        var index = new FreeSpaceIndex([new FreeRun(0, 5), new FreeRun(200, 12), new FreeRun(400, 50)]);
        Assert.Equal(200, index.FirstFit(10));
        Assert.Equal(0, index.FirstFit(5));
        Assert.Equal(205, index.FirstFit(5, from: 205));
        Assert.Equal(400, index.FirstFit(20, from: 100, to: 500));
        Assert.Null(index.FirstFit(20, from: 100, to: 410));
    }

    [Fact]
    public void Allocate_splits_and_release_merges_runs()
    {
        var index = new FreeSpaceIndex([new FreeRun(0, 100)]);
        index.Allocate(40, 20);
        Assert.Equal([new FreeRun(0, 40), new FreeRun(60, 40)], index.Runs);
        Assert.Equal(80, index.TotalFree);
        Assert.False(index.IsFree(39, 2));
        index.Release(40, 20);
        Assert.Equal([new FreeRun(0, 100)], index.Runs);
        index.Release(100, 10);
        Assert.Equal([new FreeRun(0, 110)], index.Runs);
        Assert.Equal(new FreeRun(0, 110), index.Largest);
    }

    [Fact]
    public void Move_steps_never_exceed_the_chunk_limit()
    {
        MoveStep[] steps = [.. MoveChunks.Plan(40_000, 1_000)];
        Assert.Equal(
            [new MoveStep(0, 1_000, 16_384), new MoveStep(16_384, 17_384, 16_384), new MoveStep(32_768, 33_768, 7_232)],
            steps);
        Assert.Equal(40_000, steps.Sum(s => s.Clusters));
        Assert.All(MoveChunks.Plan(5, 0, 2), s => Assert.True(s.Clusters <= 2));
        Assert.Empty(MoveChunks.Plan(0, 7));
    }

    [Fact]
    public void Ranges_subtract_overlaps()
    {
        List<FreeRun> left = Ranges.Subtract([new FreeRun(0, 100)], [new FreeRun(10, 10), new FreeRun(50, 60)]);
        Assert.Equal([new FreeRun(0, 10), new FreeRun(20, 30)], left);
        Assert.Equal([new FreeRun(5, 10)], Ranges.Of([new Extent(0, 5, 5), new Extent(5, 10, 5), new Extent(10, -1, 3)]));
    }

    [Fact]
    public void Map_shows_each_kind_of_block()
    {
        Assert.Equal(ClusterState.Free, DriveMap.Pick(total: 100, allocated: 0, owned: 0, fragmented: 0, archive: 0));
        Assert.Equal(ClusterState.Used, DriveMap.Pick(100, 90, 90, 0, 0));
        Assert.Equal(ClusterState.Fragmented, DriveMap.Pick(100, 90, 90, 30, 0));
        Assert.Equal(ClusterState.Archive, DriveMap.Pick(100, 90, 90, 0, 60));
        Assert.Equal(ClusterState.System, DriveMap.Pick(100, 90, 10, 0, 0));
    }

    [Fact]
    public void Map_follows_ownership_changes()
    {
        var bitmap = new ClusterBitmap(1_000);
        bitmap.Set(0, 500, allocated: true);
        DriveMap map = DriveMap.From(bitmap, segments: 10);
        Assert.Equal([ClusterState.System, ClusterState.Free], map.Summarize(2));
        long version = map.Version;
        map.Own([new Extent(0, 0, 500)], ClusterState.Fragmented, add: true);
        Assert.True(map.Version > version);
        Assert.Equal([ClusterState.Fragmented, ClusterState.Free], map.Summarize(2));
        map.Own([new Extent(0, 0, 500)], ClusterState.Fragmented, add: false);
        map.Allocate(0, 500, allocated: false);
        Assert.Equal((1_000L, 0L, 0L, 0L, 0L), map.Totals());
    }
}

public sealed class IoThrottleTests
{
    [Fact]
    public void Busy_disk_backs_off_and_quiet_disk_recovers()
    {
        var throttle = new IoThrottleController();
        Assert.Equal(IoThrottleController.Breather, throttle.Next(null, 0));
        throttle.Next(new IoSample(0, 0, 0, TimeSpan.Zero), 0);
        TimeSpan pause = throttle.Next(new IoSample(64L << 20, 0, 0, TimeSpan.FromSeconds(1)), 0);
        Assert.True(throttle.IsUserBusy);
        Assert.Equal(IoThrottleController.FirstBackoff, pause);
        TimeSpan at = TimeSpan.FromSeconds(1);
        for (int i = 0; i < 10; i++)
        {
            at += TimeSpan.FromSeconds(1);
            pause = throttle.Next(new IoSample((64L << 20) * (i + 2), 0, 0, at), 0);
        }

        Assert.Equal(IoThrottleController.LongestPause, pause);
        for (int i = 0; i < 20; i++)
        {
            at += TimeSpan.FromSeconds(1);
            pause = throttle.Next(new IoSample((64L << 20) * 11, 0, 0, at), 0);
        }

        Assert.False(throttle.IsUserBusy);
        Assert.Equal(IoThrottleController.Breather, pause);
    }

    [Fact]
    public void Own_moves_do_not_count_as_user_activity()
    {
        var throttle = new IoThrottleController();
        throttle.Next(new IoSample(0, 0, 0, TimeSpan.Zero), 0);
        throttle.Next(new IoSample(32L << 20, 32L << 20, 0, TimeSpan.FromSeconds(1)), 32L << 20);
        Assert.False(throttle.IsUserBusy);
        throttle.Next(new IoSample(32L << 20, 32L << 20, IoThrottleController.BusyQueueDepth, TimeSpan.FromSeconds(2)), 0);
        Assert.True(throttle.IsUserBusy);
    }
}

public sealed class DriveActionTests
{
    [Theory]
    [InlineData(MediaKind.Hdd, "NTFS", DriveAction.Defragment)]
    [InlineData(MediaKind.Hdd, "exFAT", DriveAction.Defragment)]
    [InlineData(MediaKind.Hdd, "ReFS", DriveAction.AnalyzeOnly)]
    [InlineData(MediaKind.Ssd, "NTFS", DriveAction.Trim)]
    [InlineData(MediaKind.Unknown, "NTFS", DriveAction.AnalyzeOnly)]
    public void Media_picks_the_default_action(MediaKind media, string fileSystem, DriveAction action) =>
        Assert.Equal(action, new FakeDisk(64, media, fileSystem).Volume.DefaultAction);

    [Fact]
    public void Ssd_volumes_refuse_every_cluster_move()
    {
        var disk = new FakeDisk(256, MediaKind.Ssd).File("a.dll", (10, 2), (50, 2));
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Throws<DriveRelocationRefusedException>(() => DriveDefragmenter.Guard(disk.Volume));
        Assert.Throws<DriveRelocationRefusedException>(() => DefragPlanner.Plan(analysis, DefragMode.Quick));
        Assert.Throws<DriveRelocationRefusedException>(() => new DriveDefragmenter(disk).Run(analysis, DefragMode.FullZone, null, null, CancellationToken.None));
        Assert.Throws<DriveRelocationRefusedException>(() => { _ = new DriveOptimizer(disk).DefragmentAsync(analysis, DefragMode.Quick, null, null, CancellationToken.None); });
        Assert.Empty(disk.Moves);
    }

    [Fact]
    public void Hard_disks_never_get_trim()
    {
        var disk = new FakeDisk(64);
        Assert.Throws<InvalidOperationException>(() => new DriveTrimmer(disk).Run(disk.Volume, null, CancellationToken.None));
    }

    [Fact]
    public async Task Ssd_trim_runs_file_level_first()
    {
        var disk = new FakeDisk(16L << 20, MediaKind.Ssd);
        var seen = new List<double>();
        TrimResult result = await new DriveOptimizer(disk).TrimAsync(disk.Volume, new Inline(p => seen.Add(p.Percent)), CancellationToken.None);
        Assert.Equal(TrimOutcome.Trimmed, result.Outcome);
        Assert.Equal(TrimMethod.FileLevel, result.Method);
        Assert.Equal(DriveTrimmer.FileLevelBytes(disk.Volume), disk.NativeTrimmed);
        Assert.Equal(0, disk.RetrimCalls);
        Assert.Equal(100, seen[^1]);
        Assert.False(disk.InBackground);
    }

    [Fact]
    public void Ssd_trim_falls_back_to_the_system_retrim()
    {
        var disk = new FakeDisk(16L << 20, MediaKind.Ssd) { NativeTrimError = 326 };
        TrimResult result = new DriveTrimmer(disk).Run(disk.Volume, null, CancellationToken.None);
        Assert.Equal(TrimOutcome.Trimmed, result.Outcome);
        Assert.Equal(TrimMethod.SystemRetrim, result.Method);
        Assert.Equal(1, disk.RetrimCalls);

        disk.RetrimExit = 5;
        Assert.Equal(TrimOutcome.Failed, new DriveTrimmer(disk).Run(disk.Volume, null, CancellationToken.None).Outcome);
        var bare = new FakeDisk(64, MediaKind.Ssd, trim: false);
        Assert.Equal(TrimOutcome.NotSupported, new DriveTrimmer(bare).Run(bare.Volume, null, CancellationToken.None).Outcome);
    }

    [Fact]
    public void Trim_keeps_headroom_free()
    {
        var disk = new FakeDisk(16L << 20, MediaKind.Ssd);
        long bytes = DriveTrimmer.FileLevelBytes(disk.Volume);
        Assert.True(disk.Volume.FreeBytes - bytes >= DriveTrimmer.Headroom(disk.Volume));
        Assert.Equal(0, bytes % disk.Volume.BytesPerCluster);
    }

    [Theory]
    [InlineData("Retrim:  42% complete...", 42d)]
    [InlineData("Pemangkasan ulang: 100 % selesai", 100d)]
    [InlineData("Volume C: 0% free", 0d)]
    [InlineData("No percent here", null)]
    [InlineData("1234%", null)]
    public void Retrim_output_gives_its_percentage(string line, double? percent) => Assert.Equal(percent, RetrimOutput.Percent(line));

    private sealed class Inline(Action<DriveProgress> report) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => report(value);
    }
}

public sealed class DriveAnalyzerTests
{
    private static FakeDisk SampleDisk() => new FakeDisk(1_000)
        .Reserve(0, 20)
        .File("pagefile.sys", (20, 30))
        .Folder("System Volume Information")
        .File(@"System Volume Information\snap", (50, 5))
        .Folder("Apps")
        .File(@"Apps\tool.exe", (100, 4), (200, 4))
        .File(@"Apps\film.mkv", (300, 40))
        .File(@"Apps\notes.txt", (400, 3))
        .Link("Junction")
        .File(@"Junction\loop.txt", (500, 2));

    [Fact]
    public void Analysis_counts_fragments_and_leaves_protected_files_alone()
    {
        FakeDisk disk = SampleDisk();
        var phases = new List<DrivePhase>();
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, new Inline(p => phases.Add(p.Phase)), CancellationToken.None);
        Assert.Equal([@"X:\Apps\film.mkv", @"X:\Apps\notes.txt", @"X:\Apps\tool.exe"], analysis.Files.Select(f => f.Path).Order());
        DriveFile tool = analysis.Files.Single(f => f.Path.EndsWith("tool.exe", StringComparison.Ordinal));
        Assert.Equal(2, tool.Fragments);
        Assert.Equal(SpeedZone.Fast, tool.Zone);
        Assert.Equal(SpeedZone.Archive, analysis.Files.Single(f => f.Path.EndsWith(".mkv", StringComparison.Ordinal)).Zone);
        DriveSummary summary = analysis.Summary();
        Assert.Equal(1, summary.FragmentedFiles);
        Assert.Equal(1, summary.ExcessFragments);
        Assert.Equal(2, summary.Protected);
        Assert.Equal((1, 1, 1), (summary.FastFiles, summary.StandardFiles, summary.ArchiveFiles));
        Assert.Equal(8 * 100.0 / 51, summary.FragmentedPercent, 3);
        Assert.Equal(DrivePhase.Finished, phases[^1]);
        (long free, long used, long fragmented, long archive, long system) = analysis.Map.Totals();
        Assert.Equal(8, fragmented);
        Assert.Equal(40, archive);
        Assert.Equal(3, used);
        Assert.Equal(20 + 30 + 5 + 2, system);
        Assert.Equal(1_000 - 8 - 40 - 3 - 57, free);
    }

    [Fact]
    public void Summary_reports_the_largest_free_block()
    {
        FakeDisk disk = SampleDisk();
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Equal(1_000 - 502, analysis.Summary().LargestFreeClusters);
    }

    private sealed class Inline(Action<DriveProgress> report) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => report(value);
    }
}

public sealed class DriveDefragmenterTests
{
    private static (FakeDisk Disk, DriveAnalysis Analysis) Analyze(FakeDisk disk) =>
        (disk, new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None));

    [Fact]
    public void Quick_defrag_joins_fragmented_files_and_updates_the_map()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(1_000).File("a.dat", (10, 5), (40, 5), (80, 5)).File("b.dat", (100, 3)));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 1, 1, 0, 0), (result.Planned, result.Moved, result.Defragmented, result.Locked, result.Failed));
        IReadOnlyList<Extent> moved = disk.ExtentsOf("a.dat");
        Assert.Single(moved);
        Assert.Equal(1, analysis.Files.Single(f => f.Path.EndsWith("a.dat", StringComparison.Ordinal)).Fragments);
        foreach (long old in (long[])[10, 40, 80])
        {
            Assert.False(disk.IsUsed(old));
            Assert.False(analysis.Bitmap.IsAllocated(old));
            Assert.True(analysis.Free.IsFree(old, 5));
        }

        Assert.True(analysis.Bitmap.IsAllocated(moved[0].Lcn));
        Assert.Equal(0, analysis.Map.Totals().Fragmented);
        Assert.Equal(0, analysis.Summary().FragmentedFiles);
        Assert.Equal(analysis.Bitmap.Clusters - 18, analysis.Free.TotalFree);
    }

    [Fact]
    public void Large_files_move_in_chunks()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(100_000).File("big.vhdx", (0, 20_000), (30_000, 20_000)));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal(1, result.Defragmented);
        Assert.Equal(40_000, result.ClustersMoved);
        Assert.Equal(3, disk.Moves.Count);
        Assert.All(disk.Moves, m => Assert.True(m.Clusters <= MoveChunks.MaxClusters));
        Assert.Single(disk.ExtentsOf("big.vhdx"));
    }

    [Fact]
    public void Defrag_checked_moves_only_the_chosen_files()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(1_000)
            .Folder("Keep").File(@"Keep\a.dat", (10, 2), (20, 2))
            .Folder("Pick").File(@"Pick\b.dat", (30, 2), (40, 2)));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, [@"X:\Pick"], null, CancellationToken.None);
        Assert.Equal(1, result.Defragmented);
        Assert.Equal(2, disk.ExtentsOf(@"Keep\a.dat").Count);
        Assert.Single(disk.ExtentsOf(@"Pick\b.dat"));
        Assert.True(DefragPlanner.Selected(@"X:\Pick\b.dat", [@"X:\Pick\"]));
        Assert.False(DefragPlanner.Selected(@"X:\Picker\b.dat", [@"X:\Pick"]));
    }

    [Fact]
    public void Files_in_use_are_counted_and_skipped()
    {
        FakeDisk disk = new FakeDisk(1_000).File("open.dat", (10, 2), (20, 2)).File("free.dat", (30, 2), (40, 2));
        disk.Locked.Add(@"X:\open.dat");
        (_, DriveAnalysis analysis) = Analyze(disk);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 1, 0), (result.Locked, result.Defragmented, result.Failed));
        Assert.Equal(2, disk.ExtentsOf("open.dat").Count);
        Assert.Equal(1, analysis.Summary().Locked);
    }

    [Fact]
    public void Consolidate_fills_gaps_with_small_files()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(1_000).File("first.txt", (0, 10)).File("late.txt", (500, 10)));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Consolidate, null, null, CancellationToken.None);
        Assert.Equal(1, result.Moved);
        Assert.Equal(10, disk.ExtentsOf("late.txt")[0].Lcn);
        Assert.Equal(1_000 - 20, analysis.Free.Largest.Length);
    }

    [Fact]
    public void Full_zone_optimization_places_files_by_speed()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(10_000)
            .File("disc.iso", (100, 50))
            .File("app.exe", (6_000, 20))
            .File("doc.txt", (9_990, 5)));
        ZoneLayout layout = analysis.Layout;
        new DriveDefragmenter(disk).Run(analysis, DefragMode.FullZone, null, null, CancellationToken.None);
        Extent iso = disk.ExtentsOf("disc.iso")[0];
        Extent exe = disk.ExtentsOf("app.exe")[0];
        Extent doc = disk.ExtentsOf("doc.txt")[0];
        Assert.True(layout.Holds(SpeedZone.Archive, iso.Lcn, 50), $"iso at {iso.Lcn}");
        Assert.True(layout.Holds(SpeedZone.Fast, exe.Lcn, 20), $"exe at {exe.Lcn}");
        Assert.True(layout.Holds(SpeedZone.Standard, doc.Lcn, 5), $"doc at {doc.Lcn}");
    }

    [Fact]
    public void A_taken_target_is_skipped_on_the_second_try()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 3), (20, 3));
        (_, DriveAnalysis analysis) = Analyze(disk);
        long first = analysis.Free.BestFit(6)!.Value;
        disk.Reserve(first, 6);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal(1, result.Defragmented);
        Assert.NotEqual(first, disk.ExtentsOf("a.dat")[0].Lcn);
    }

    [Fact]
    public void A_target_taken_after_the_analysis_is_marked_used_and_the_file_is_not_called_locked()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 3), (20, 3));
        (_, DriveAnalysis analysis) = Analyze(disk);
        long first = analysis.Free.BestFit(6)!.Value;
        disk.Reserve(first, 6);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 1, 0, 0), (result.Moved, result.Defragmented, result.Locked, result.Failed));
        Assert.True(disk.RangeReads >= 1);
        Assert.Equal(6, analysis.Bitmap.CountAllocated(first, 6));
        Assert.False(analysis.Free.IsFree(first, 1));
        Assert.Equal(0, analysis.Summary().Locked);
        Assert.Single(disk.ExtentsOf("a.dat"));
        Assert.Equal(analysis.Bitmap.Clusters - analysis.Bitmap.CountAllocated(0, analysis.Bitmap.Clusters), analysis.Free.TotalFree);
    }

    [Fact]
    public void A_partly_taken_target_gives_its_free_part_back()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 3), (20, 3));
        (_, DriveAnalysis analysis) = Analyze(disk);
        long first = analysis.Free.BestFit(6)!.Value;
        disk.Reserve(first + 4, 2);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 0), (result.Defragmented, result.Locked));
        Assert.True(analysis.Bitmap.IsAllocated(first + 4));
        Assert.True(analysis.Free.IsFree(first, 4));
        Assert.Single(disk.ExtentsOf("a.dat"));
        Assert.Equal(analysis.Bitmap.Clusters - analysis.Bitmap.CountAllocated(0, analysis.Bitmap.Clusters), analysis.Free.TotalFree);
    }

    [Fact]
    public void A_large_file_whose_later_chunk_target_is_taken_is_moved_again_until_it_is_whole()
    {
        FakeDisk disk = new FakeDisk(200_000).File("big.vhdx", (0, 20_000), (30_000, 20_000));
        (_, DriveAnalysis analysis) = Analyze(disk);
        long first = analysis.Free.BestFit(40_000)!.Value;
        disk.Reserve(first + 20_000, 10);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((1, 1, 0, 0), (result.Moved, result.Defragmented, result.Locked, result.Failed));
        Assert.Single(disk.ExtentsOf("big.vhdx"));
        Assert.Equal(1, analysis.Files.Single().Fragments);
        Assert.True(analysis.Bitmap.IsAllocated(first + 20_000));
    }

    [Fact]
    public void A_file_the_file_system_refuses_to_move_is_locked_and_its_target_is_released()
    {
        FakeDisk disk = new FakeDisk(1_000).File("pinned.dat", (10, 3), (20, 3));
        disk.Pinned.Add(@"X:\pinned.dat");
        (_, DriveAnalysis analysis) = Analyze(disk);
        long free = analysis.Free.TotalFree;
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal((0, 1, 0), (result.Moved, result.Locked, result.Failed));
        Assert.Single(disk.Moves);
        Assert.Equal(free, analysis.Free.TotalFree);
        Assert.Equal(2, disk.ExtentsOf("pinned.dat").Count);
        Assert.Equal(1, analysis.Summary().Locked);
    }

    [Theory]
    [InlineData(0, 100, 0, 0, 0)]
    [InlineData(40, 100, 0, 0, 40)]
    [InlineData(40, 100, 500_000, 1_000_000, 45)]
    [InlineData(41, 100, 900_000, 1_000_000, 65.5)]
    [InlineData(100, 100, 2_000_000, 1_000_000, 99)]
    [InlineData(5, 0, 10, 0, 99)]
    public void Scan_progress_moves_with_files_even_when_they_are_tiny(long scanned, long used, long seen, long expected, double percent) =>
        Assert.Equal(percent, ScanProgress.Percent(scanned, used, seen, expected), 3);

    [Fact]
    public void Scan_progress_never_goes_back_while_entries_and_clusters_grow()
    {
        double last = 0;
        for (long step = 0; step <= 1_000; step++)
        {
            double now = ScanProgress.Percent(Math.Min(400, step / 3), 400, step * 1_700, 1_000_000);
            Assert.True(now >= last, $"step {step}: {now} < {last}");
            Assert.InRange(now, 0, ScanProgress.Ceiling);
            last = now;
        }
    }

    [Fact]
    public void Cancelling_an_analysis_stops_inside_a_large_folder()
    {
        FakeDisk disk = new FakeDisk(10_000).Folder("Backup");
        for (int i = 0; i < 2_000; i++)
        {
            disk.File($@"Backup\{i:x8}", (i * 2, 1));
        }

        using var cancel = new CancellationTokenSource();
        disk.OnRead = reads =>
        {
            if (reads == 10)
            {
                cancel.Cancel();
            }
        };
        Assert.Throws<OperationCanceledException>(() => new DriveAnalyzer(disk).Analyze(disk.Volume, null, cancel.Token));
        Assert.InRange(disk.Reads, 10, 11);
    }

    [Fact]
    public void An_analysis_backs_off_while_the_disk_is_busy()
    {
        FakeDisk disk = new FakeDisk(10_000) { BusyQueue = IoThrottleController.BusyQueueDepth };
        for (int i = 0; i < 3 * DriveAnalyzer.YieldEvery; i++)
        {
            disk.File($"f{i:x8}", (i * 2, 1));
        }

        IoThrottleController? used = null;
        new DriveAnalyzer(disk, throttle: () => used = new IoThrottleController()).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.NotNull(used);
        Assert.True(used.IsUserBusy);
        Assert.True(used.Pause >= IoThrottleController.FirstBackoff);
    }

    [Fact]
    public void An_idle_disk_is_analyzed_without_pauses()
    {
        FakeDisk disk = new FakeDisk(10_000) { BusyQueue = 0 };
        for (int i = 0; i < 3 * DriveAnalyzer.YieldEvery; i++)
        {
            disk.File($"f{i:x8}", (i * 2, 1));
        }

        IoThrottleController? used = null;
        new DriveAnalyzer(disk, throttle: () => used = new IoThrottleController()).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.NotNull(used);
        Assert.False(used.IsUserBusy);
        Assert.Equal(IoThrottleController.Breather, used.Pause);
    }

    [Fact]
    public void Files_of_one_folder_share_the_folder_text()
    {
        (_, DriveAnalysis analysis) = Analyze(new FakeDisk(1_000).Folder("Data").File(@"Data\a.dat", (10, 2)).File(@"Data\b.dat", (20, 2)).File("root.dat", (30, 2)));
        DriveFile a = analysis.Files.Single(f => f.Name == "a.dat");
        DriveFile b = analysis.Files.Single(f => f.Name == "b.dat");
        Assert.Same(a.Folder, b.Folder);
        Assert.Equal(@"X:\Data\a.dat", a.Path);
        Assert.Equal(@"X:\root.dat", analysis.Files.Single(f => f.Name == "root.dat").Path);
    }

    [Fact]
    public void Allocated_runs_are_read_from_a_bitmap_that_starts_before_the_range()
    {
        byte[] bits = [0b1111_0000, 0b0000_0011, 0b0000_1000];
        var runs = new List<FreeRun>();
        AllocatedRuns.Collect(bits, 8, 10, 20, runs);
        Assert.Equal([new FreeRun(12, 6), new FreeRun(27, 1)], runs);
        runs.Clear();
        AllocatedRuns.Collect(bits, 8, 30, 5, runs);
        Assert.Empty(runs);
    }

    [Fact]
    public void Free_runs_can_be_listed_inside_a_range()
    {
        var bitmap = new ClusterBitmap(100);
        bitmap.Set(12, 3, allocated: true);
        bitmap.Set(18, 1, allocated: true);
        Assert.Equal([new FreeRun(10, 2), new FreeRun(15, 3), new FreeRun(19, 1)], bitmap.FreeRuns(10, 10));
        Assert.Empty(bitmap.FreeRuns(12, 3));
    }

    [Fact]
    public void Cancelling_stops_between_files()
    {
        (FakeDisk disk, DriveAnalysis analysis) = Analyze(new FakeDisk(1_000).File("a.dat", (10, 2), (20, 2)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(disk.Moves);
    }

    [Fact]
    public async Task Work_runs_on_a_background_worker()
    {
        FakeDisk disk = new FakeDisk(1_000).File("a.dat", (10, 2), (20, 2));
        var optimizer = new DriveOptimizer(disk);
        DriveAnalysis analysis = await optimizer.AnalyzeAsync(disk.Volume, null, CancellationToken.None);
        Assert.Equal(1, disk.Opened);
        Assert.False(disk.OpenedInBackground);
        DefragResult result = await optimizer.DefragmentAsync(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal(1, result.Defragmented);
        Assert.True(disk.OpenedInBackground);
        Assert.False(disk.InBackground);
    }
}
