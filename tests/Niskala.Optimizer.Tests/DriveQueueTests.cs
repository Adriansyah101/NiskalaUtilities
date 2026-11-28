using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class DriveQueueTests
{
    private static FakeDisk Hdd(char letter) => new FakeDisk(1_000, letter: letter).File("split.dat", (10, 4), (500, 4)).File("other.dat", (100, 8));

    private static FakeDisk Ssd(char letter, bool trim = true) => new FakeDisk(1_000, MediaKind.Ssd, trim: trim, letter: letter).File("split.dat", (10, 4), (500, 4));

    [Fact]
    public void Analysis_queues_every_drive_in_letter_order_once()
    {
        FakeDisk d = Hdd('D'), c = Ssd('C');
        IReadOnlyList<QueueStep> steps = DriveQueue.Plan([d.Volume, c.Volume, d.Volume], QueueKind.Analyze, DefragMode.FullZone);

        Assert.Equal(['C', 'D'], steps.Select(s => s.Volume.Letter));
        Assert.All(steps, s => Assert.Equal(QueueStepKind.Analyze, s.Kind));
    }

    [Fact]
    public void Optimization_gives_ssds_trim_only_and_hard_disks_the_chosen_mode()
    {
        FakeDisk c = Ssd('C'), d = Hdd('D'), e = Ssd('E', trim: false);
        var unknown = new FakeDisk(100, MediaKind.Unknown, letter: 'F');
        var refs = new FakeDisk(100, fileSystem: "ReFS", letter: 'G');

        IReadOnlyList<QueueStep> steps = DriveQueue.Plan([c.Volume, d.Volume, e.Volume, unknown.Volume, refs.Volume], QueueKind.Optimize, DefragMode.Consolidate);

        Assert.Equal(
            [
                (QueueStepKind.Trim, QueueSkipReason.None),
                (QueueStepKind.Defragment, QueueSkipReason.None),
                (QueueStepKind.Skip, QueueSkipReason.NoTrim),
                (QueueStepKind.Skip, QueueSkipReason.UnknownMedia),
                (QueueStepKind.Skip, QueueSkipReason.UnsupportedFileSystem),
            ],
            steps.Select(s => (s.Kind, s.Skip)));
        Assert.Equal(DefragMode.Consolidate, steps[1].Mode);
        Assert.DoesNotContain(steps, s => s.Kind == QueueStepKind.Defragment && s.Volume.Media != MediaKind.Hdd);
    }

    [Fact]
    public void The_guard_refuses_cluster_moves_on_ssds_and_trim_on_hard_disks()
    {
        Assert.False(DriveQueue.IsAllowed(new QueueStep(Ssd('C').Volume, QueueStepKind.Defragment, DefragMode.FullZone)));
        Assert.False(DriveQueue.IsAllowed(new QueueStep(Hdd('D').Volume, QueueStepKind.Trim)));
        Assert.False(DriveQueue.IsAllowed(new QueueStep(new FakeDisk(10, MediaKind.Unknown).Volume, QueueStepKind.Defragment)));
        Assert.True(DriveQueue.IsAllowed(new QueueStep(Ssd('C').Volume, QueueStepKind.Trim)));
        Assert.True(DriveQueue.IsAllowed(new QueueStep(Hdd('D').Volume, QueueStepKind.Defragment)));
        Assert.True(DriveQueue.IsAllowed(new QueueStep(Ssd('C').Volume, QueueStepKind.Analyze)));
    }

    [Fact]
    public async Task A_mixed_queue_trims_the_ssd_defragments_the_hard_disk_and_never_moves_ssd_clusters()
    {
        FakeDisk c = Ssd('C'), d = Hdd('D');
        var fleet = new Fleet(c, d);
        var optimizer = new DriveOptimizer(fleet);
        var seen = new List<(int Index, char Letter)>();
        IReadOnlyList<QueueStep> steps = DriveQueue.Plan([d.Volume, c.Volume], QueueKind.Optimize, DefragMode.Quick);

        IReadOnlyList<QueueResult> results = await new DriveQueueRunner(optimizer).RunAsync(steps, new Inline(p => seen.Add((p.Index, p.Step.Volume.Letter))), TestContext.Current.CancellationToken);

        Assert.Equal([QueueOutcome.Done, QueueOutcome.Done], results.Select(r => r.Outcome));
        Assert.Equal(TrimOutcome.Trimmed, results[0].Trim?.Outcome);
        Assert.Equal(1, results[1].Defrag?.Defragmented);
        Assert.Empty(c.Moves);
        Assert.NotEmpty(d.Moves);
        Assert.Single(d.ExtentsOf("split.dat"));
        Assert.Equal(2, c.ExtentsOf("split.dat").Count);
        Assert.Equal(1, c.RetrimCalls);
        Assert.Equal(0, d.RetrimCalls);
        Assert.Contains((0, 'C'), seen);
        Assert.Contains((1, 'D'), seen);
        Assert.True(seen.IndexOf((0, 'C')) < seen.IndexOf((1, 'D')));
    }

    [Fact]
    public async Task A_forged_defragment_step_for_an_ssd_is_refused_without_touching_it()
    {
        FakeDisk c = Ssd('C');
        var optimizer = new DriveOptimizer(new Fleet(c));
        QueueStep forged = new(c.Volume, QueueStepKind.Defragment, DefragMode.FullZone);

        IReadOnlyList<QueueResult> results = await new DriveQueueRunner(optimizer).RunAsync([forged], null, TestContext.Current.CancellationToken);

        Assert.Equal(QueueOutcome.Refused, Assert.Single(results).Outcome);
        Assert.Empty(c.Moves);
        Assert.Equal(0, c.Opened);
    }

    [Fact]
    public async Task Analysis_results_land_in_the_shared_cache()
    {
        FakeDisk c = Ssd('C'), d = Hdd('D');
        var optimizer = new DriveOptimizer(new Fleet(c, d));

        IReadOnlyList<QueueResult> results = await new DriveQueueRunner(optimizer).RunAsync(
            DriveQueue.Plan([c.Volume, d.Volume], QueueKind.Analyze, DefragMode.Quick), null, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(QueueOutcome.Done, r.Outcome));
        Assert.Same(results[0].Analysis, optimizer.Cached(c.Volume));
        Assert.Same(results[1].Analysis, optimizer.Cached(d.Volume));
        Assert.Empty(c.Moves);
        Assert.Empty(d.Moves);
    }

    [Fact]
    public async Task Cancelling_stops_the_queue_and_marks_the_rest_cancelled()
    {
        FakeDisk c = Ssd('C'), d = Hdd('D'), e = Hdd('E');
        var optimizer = new DriveOptimizer(new Fleet(c, d, e));
        using var cancel = new CancellationTokenSource();
        IReadOnlyList<QueueStep> steps = DriveQueue.Plan([c.Volume, d.Volume, e.Volume], QueueKind.Analyze, DefragMode.Quick);

        IReadOnlyList<QueueResult> results = await new DriveQueueRunner(optimizer).RunAsync(
            steps,
            new Inline(p =>
            {
                if (p.Index == 1)
                {
                    cancel.Cancel();
                }
            }),
            cancel.Token);

        Assert.Equal(QueueOutcome.Done, results[0].Outcome);
        Assert.Equal(QueueOutcome.Cancelled, results[1].Outcome);
        Assert.Equal(QueueOutcome.Cancelled, results[2].Outcome);
        Assert.Null(optimizer.Cached(e.Volume));
    }

    [Fact]
    public async Task A_failing_drive_does_not_stop_the_drives_after_it()
    {
        FakeDisk c = Ssd('C'), d = Hdd('D');
        var fleet = new Fleet(c, d) { Broken = 'C' };
        var optimizer = new DriveOptimizer(fleet);

        IReadOnlyList<QueueResult> results = await new DriveQueueRunner(optimizer).RunAsync(
            DriveQueue.Plan([c.Volume, d.Volume], QueueKind.Analyze, DefragMode.Quick), null, TestContext.Current.CancellationToken);

        Assert.Equal(QueueOutcome.Failed, results[0].Outcome);
        Assert.False(string.IsNullOrWhiteSpace(results[0].Error));
        Assert.Equal(QueueOutcome.Done, results[1].Outcome);
    }

    [Fact]
    public async Task Skipped_drives_are_reported_and_left_alone()
    {
        FakeDisk e = Ssd('E', trim: false);
        var optimizer = new DriveOptimizer(new Fleet(e));

        QueueResult result = Assert.Single(await new DriveQueueRunner(optimizer).RunAsync(
            DriveQueue.Plan([e.Volume], QueueKind.Optimize, DefragMode.Quick), null, TestContext.Current.CancellationToken));

        Assert.Equal(QueueOutcome.Skipped, result.Outcome);
        Assert.Equal(QueueSkipReason.NoTrim, result.Step.Skip);
        Assert.Equal(0, e.Opened);
        Assert.Equal(0, e.RetrimCalls);
    }

    private sealed class Inline(Action<QueueProgress> report) : IProgress<QueueProgress>
    {
        public void Report(QueueProgress value) => report(value);
    }

    private sealed class Fleet(params FakeDisk[] disks) : IVolumeSystem
    {
        public char? Broken { get; init; }

        public IReadOnlyList<DriveVolume> FixedVolumes() => [.. disks.Select(d => d.Volume)];

        public int TryOpen(DriveVolume volume, out IVolumeSession? session)
        {
            session = null;
            return volume.Letter == Broken ? Interop.Volumes.VolumeNative.ErrorSharingViolation : Disk(volume).TryOpen(volume, out session);
        }

        public void EnterBackground()
        {
            foreach (FakeDisk disk in disks)
            {
                disk.EnterBackground();
            }
        }

        public void LeaveBackground()
        {
            foreach (FakeDisk disk in disks)
            {
                disk.LeaveBackground();
            }
        }

        public int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken) =>
            Disk(volume).RetrimWithSystemTool(volume, percent, cancellationToken);

        private FakeDisk Disk(DriveVolume volume) => disks.Single(d => d.Volume.Letter == volume.Letter);
    }
}
