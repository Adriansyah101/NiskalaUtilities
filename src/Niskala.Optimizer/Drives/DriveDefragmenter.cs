using System.Diagnostics;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public enum MovePurpose
{
    Defragment,
    Place,
    Compact,
}

public readonly record struct PlannedMove(DriveFile File, MovePurpose Purpose, SpeedZone Zone, long Ceiling = long.MaxValue);

public static class DefragPlanner
{
    public const long CompactMaxBytes = 64L << 20;

    private static readonly SpeedZone[] PlacementOrder = [SpeedZone.Archive, SpeedZone.Standard, SpeedZone.Fast];

    public static IReadOnlyList<PlannedMove> Plan(DriveAnalysis analysis, DefragMode mode, IReadOnlyCollection<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        DriveDefragmenter.Guard(analysis.Volume);
        DriveFile[] movable = [.. analysis.Files.Where(f => f.IsMovable && !f.IsLocked && (only is null || Selected(f.Path, only)))];
        var plan = new List<PlannedMove>();
        if (mode == DefragMode.FullZone)
        {
            ZoneLayout layout = analysis.Layout;
            foreach (SpeedZone zone in PlacementOrder)
            {
                plan.AddRange(movable
                    .Where(f => f.Zone == zone && (f.IsFragmented || !layout.Holds(zone, f.StartLcn, f.Clusters)))
                    .OrderBy(f => f.StartLcn)
                    .Select(f => new PlannedMove(f, MovePurpose.Place, zone)));
            }
        }
        else if (mode == DefragMode.Consolidate)
        {
            PlanConsolidation(analysis, movable, plan);
            return plan;
        }
        else
        {
            plan.AddRange(movable
                .Where(f => f.IsFragmented)
                .OrderByDescending(f => f.Fragments)
                .ThenBy(f => f.Clusters)
                .Select(f => new PlannedMove(f, MovePurpose.Defragment, f.Zone)));
        }

        if (mode != DefragMode.Quick)
        {
            var planned = plan.Select(p => p.File).ToHashSet();
            plan.AddRange(movable
                .Where(f => f.Size <= CompactMaxBytes && (planned.Contains(f) || !f.IsFragmented))
                .OrderBy(f => f.StartLcn)
                .Select(f => new PlannedMove(f, MovePurpose.Compact, f.Zone)));
        }

        return plan;
    }

    public static long DataEnd(DriveAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.Free.DataEnd(analysis.Bitmap.Clusters);
    }

    private static void PlanConsolidation(DriveAnalysis analysis, DriveFile[] movable, List<PlannedMove> plan)
    {
        long ceiling = DataEnd(analysis);
        DriveFile[] fragmented = [.. movable.Where(f => f.IsFragmented).OrderBy(f => f.StartLcn)];
        plan.AddRange(movable
            .Where(f => !f.IsFragmented && f.Size <= CompactMaxBytes)
            .OrderBy(f => f.StartLcn)
            .Select(f => new PlannedMove(f, MovePurpose.Compact, f.Zone)));
        if (fragmented.Length == 0)
        {
            return;
        }

        plan.AddRange(fragmented.Select(f => new PlannedMove(f, MovePurpose.Defragment, f.Zone, ceiling)));
        long lowestHole = fragmented.Min(f => f.Extents.Where(e => !e.IsVirtual).Min(e => e.Lcn));
        var joined = fragmented.ToHashSet();
        plan.AddRange(movable
            .Where(f => f.Size <= CompactMaxBytes && (joined.Contains(f) || f.StartLcn > lowestHole))
            .OrderBy(f => f.StartLcn)
            .Select(f => new PlannedMove(f, MovePurpose.Compact, f.Zone)));
        plan.AddRange(fragmented.Select(f => new PlannedMove(f, MovePurpose.Defragment, f.Zone, ceiling)));
    }

    public static long? Target(DriveAnalysis analysis, PlannedMove move, DefragMode mode)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        DriveFile file = move.File;
        long clusters = file.Clusters;
        if (clusters <= 0)
        {
            return null;
        }

        FreeSpaceIndex free = analysis.Free;
        ZoneLayout layout = analysis.Layout;
        switch (move.Purpose)
        {
            case MovePurpose.Defragment:
                return mode == DefragMode.Quick ? free.BestFit(clusters) : free.FirstFit(clusters, 0, move.Ceiling);
            case MovePurpose.Place:
                (long from, long to) = layout.Range(move.Zone);
                return free.FirstFit(clusters, from, to) ?? (file.IsFragmented ? free.BestFit(clusters) : null);
            default:
                long floor = mode == DefragMode.FullZone ? layout.Range(move.Zone).From : 0;
                return file.StartLcn > floor ? free.FirstFit(clusters, floor, file.StartLcn) : null;
        }
    }

    public static bool Selected(string path, IReadOnlyCollection<string>? only)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (only is null)
        {
            return true;
        }

        foreach (string chosen in only)
        {
            string root = Path.TrimEndingDirectorySeparator(chosen);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class DriveDefragmenter(IVolumeSystem system, Func<IoThrottleController>? throttle = null)
{
    public const long FinishLimitBytes = 2L << 30;

    private const int Attempts = 3;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(120);

    private readonly Func<IoThrottleController> _throttle = throttle ?? (() => new IoThrottleController());

    public static void Guard(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.Media == MediaKind.Ssd)
        {
            throw new DriveRelocationRefusedException($"{volume.Name} is a solid-state drive; clusters are never moved on it. Use TRIM instead.");
        }

        if (!volume.CanRelocate)
        {
            throw new DriveRelocationRefusedException($"{volume.Name} ({volume.FileSystem}, {volume.Media}) is not a hard disk this optimizer can defragment.");
        }
    }

    public DefragResult Run(DriveAnalysis analysis, DefragMode mode, IReadOnlyCollection<string>? only, IProgress<DriveProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Guard(analysis.Volume);
        IReadOnlyList<PlannedMove> plan = DefragPlanner.Plan(analysis, mode, only);
        var clock = Stopwatch.StartNew();
        int error = system.TryOpen(analysis.Volume, out IVolumeSession? opened);
        if (error != 0 || opened is null)
        {
            throw new DriveAccessException(analysis.Volume, error);
        }

        using IVolumeSession session = opened;
        IoThrottleController controller = _throttle();
        var tally = new Tally();
        var report = Stopwatch.StartNew();
        for (int index = 0; index < plan.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tally.Cancelled = true;
                break;
            }

            PlannedMove move = plan[index];
            if (report.Elapsed >= ReportEvery || index == 0)
            {
                report.Restart();
                DrivePhase phase = move.Purpose == MovePurpose.Compact ? DrivePhase.Compacting : DrivePhase.Moving;
                progress?.Report(new DriveProgress(phase, index * 100.0 / plan.Count, move.File.Path, analysis.Map.Version, analysis.Map));
            }

            bool wasFragmented = move.File.IsFragmented;
            bool stopping = false;
            double percent = index * 100.0 / plan.Count;
            MoveOutcome outcome = Execute(session, analysis, move, mode, controller, tally, Stopping, cancellationToken);
            tally.Count(move.File, outcome, outcome == MoveOutcome.Moved && wasFragmented && !move.File.IsFragmented);

            void Stopping()
            {
                if (!stopping)
                {
                    stopping = true;
                    progress?.Report(new DriveProgress(DrivePhase.Stopping, percent, move.File.Path, analysis.Map.Version, analysis.Map));
                }
            }
        }

        tally.Cancelled |= cancellationToken.IsCancellationRequested;

        progress?.Report(new DriveProgress(DrivePhase.Finished, 100, analysis.Volume.Name, analysis.Map.Version, analysis.Map));
        return new DefragResult(mode, plan.Select(p => p.File).Distinct().Count(), tally.Moved, tally.Defragmented, tally.Locked, tally.NoRoom, tally.Failed, tally.Clusters, tally.Cancelled, clock.Elapsed);
    }

    public static bool FinishesOnCancel(DriveFile file, long bytesPerCluster)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Clusters * Math.Max(1, bytesPerCluster) <= FinishLimitBytes;
    }

    private static MoveOutcome Execute(IVolumeSession session, DriveAnalysis analysis, PlannedMove move, DefragMode mode, IoThrottleController controller, Tally tally, Action stopping, CancellationToken cancellationToken)
    {
        DriveFile file = move.File;
        if (file.IsLocked)
        {
            return MoveOutcome.Locked;
        }

        if (move.Purpose == MovePurpose.Defragment && !file.IsFragmented)
        {
            return MoveOutcome.AlreadyPlaced;
        }

        bool movedAny = false;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            long? planned = DefragPlanner.Target(analysis, move, mode);
            if (planned is not null && attempt == 0 && Resync(session, analysis, file))
            {
                if (!file.IsMovable || (move.Purpose == MovePurpose.Defragment && !file.IsFragmented))
                {
                    return MoveOutcome.AlreadyPlaced;
                }

                planned = DefragPlanner.Target(analysis, move, mode);
            }

            if (planned is not { } target)
            {
                return move.Purpose == MovePurpose.Compact ? MoveOutcome.AlreadyPlaced : MoveOutcome.NoRoom;
            }

            int error = session.BeginMove(file.Path, out IFileMover? mover);
            if (error != 0 || mover is null)
            {
                return Refused(file, error);
            }

            long clusters = file.Clusters;
            long bytesPerCluster = Math.Max(1, analysis.Volume.BytesPerCluster);
            bool finish = FinishesOnCancel(file, bytesPerCluster);
            analysis.Free.Allocate(target, clusters);
            long moved = 0;
            MoveStep failed = default;
            using (mover)
            {
                foreach (MoveStep step in MoveChunks.Plan(clusters, target))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (!finish || (!movedAny && moved == 0))
                        {
                            break;
                        }

                        stopping();
                    }

                    error = mover.Move(step.Vcn, step.Lcn, step.Clusters);
                    if (error != 0)
                    {
                        failed = step;
                        break;
                    }

                    moved += step.Clusters;
                    TimeSpan pause = controller.Next(session.SampleIo(), step.Clusters * bytesPerCluster);
                    cancellationToken.WaitHandle.WaitOne(pause);
                }
            }

            bool taken = error != 0 && failed.Clusters > 0 && TakenSinceAnalysis(session, analysis, failed.Lcn, failed.Clusters);
            Extent[] before = file.Extents;
            var after = new List<Extent>();
            Extent[] now = session.ReadExtents(file.Path, after) == 0 && after.Count > 0 ? [.. after]
                : moved >= clusters ? [new Extent(0, target, clusters)]
                : before;
            Reconcile(analysis, file, before, now, target, clusters, keepReservation: moved == 0 && error != 0 && !taken && !DriveAnalyzer.IsLock(error));
            tally.Clusters += moved;
            movedAny |= moved > 0;
            if (moved > 0 && !(taken && file.IsFragmented))
            {
                return MoveOutcome.Moved;
            }

            if (taken)
            {
                if (cancellationToken.IsCancellationRequested && !(finish && movedAny))
                {
                    return movedAny ? MoveOutcome.Moved : MoveOutcome.AlreadyPlaced;
                }

                continue;
            }

            if (movedAny)
            {
                return MoveOutcome.Moved;
            }

            if (DriveAnalyzer.IsLock(error))
            {
                return Refused(file, error);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return MoveOutcome.AlreadyPlaced;
            }
        }

        return movedAny ? MoveOutcome.Moved : MoveOutcome.Failed;
    }

    internal static bool Resync(IVolumeSession session, DriveAnalysis analysis, DriveFile file)
    {
        var current = new List<Extent>();
        if (session.ReadExtents(file.Path, current) != 0 || current.SequenceEqual(file.Extents))
        {
            return false;
        }

        Reconcile(analysis, file, file.Extents, [.. current], 0, 0, keepReservation: true);
        return true;
    }

    internal static bool TakenSinceAnalysis(IVolumeSession session, DriveAnalysis analysis, long start, long clusters)
    {
        var allocated = new List<FreeRun>();
        if (session.ReadAllocated(start, clusters, allocated) != 0)
        {
            return false;
        }

        bool taken = false;
        foreach (FreeRun run in allocated)
        {
            if (analysis.Bitmap.CountAllocated(run.Start, run.Length) == 0)
            {
                analysis.Bitmap.Set(run.Start, run.Length, allocated: true);
                analysis.Map.Allocate(run.Start, run.Length, allocated: true);
                taken = true;
            }
        }

        return taken;
    }

    private static MoveOutcome Refused(DriveFile file, int error)
    {
        if (DriveAnalyzer.IsLock(error))
        {
            file.IsLocked = true;
            return MoveOutcome.Locked;
        }

        return MoveOutcome.Failed;
    }

    internal static void Reconcile(DriveAnalysis analysis, DriveFile file, Extent[] before, Extent[] after, long target, long clusters, bool keepReservation)
    {
        List<FreeRun> old = Ranges.Of(before);
        List<FreeRun> now = Ranges.Of(after);
        ClusterState was = file.State;
        analysis.Map.Own(before, was, add: false);
        foreach (FreeRun freed in Ranges.Subtract(old, now))
        {
            analysis.Bitmap.Set(freed.Start, freed.Length, allocated: false);
            analysis.Map.Allocate(freed.Start, freed.Length, allocated: false);
            analysis.Free.Release(freed.Start, freed.Length);
        }

        foreach (FreeRun gained in Ranges.Subtract(now, old))
        {
            analysis.Bitmap.Set(gained.Start, gained.Length, allocated: true);
            analysis.Map.Allocate(gained.Start, gained.Length, allocated: true);
            analysis.Free.Allocate(gained.Start, gained.Length);
        }

        if (!keepReservation)
        {
            foreach (FreeRun unused in Ranges.Subtract([new FreeRun(target, clusters)], now))
            {
                foreach (FreeRun free in analysis.Bitmap.FreeRuns(unused.Start, unused.Length))
                {
                    analysis.Free.Release(free.Start, free.Length);
                }
            }
        }

        file.Extents = after;
        analysis.Map.Own(after, file.State, add: true);
    }

    private sealed class Tally
    {
        private readonly HashSet<DriveFile> _moved = [];
        private readonly HashSet<DriveFile> _joined = [];
        private readonly HashSet<DriveFile> _locked = [];
        private readonly HashSet<DriveFile> _noRoom = [];
        private readonly HashSet<DriveFile> _failed = [];

        public int Moved => _moved.Count;

        public int Defragmented => _joined.Count;

        public int Locked => _locked.Count;

        public int NoRoom => _noRoom.Count;

        public int Failed => _failed.Count;

        public long Clusters { get; set; }

        public bool Cancelled { get; set; }

        public void Count(DriveFile file, MoveOutcome outcome, bool joined)
        {
            switch (outcome)
            {
                case MoveOutcome.Moved:
                    _moved.Add(file);
                    _noRoom.Remove(file);
                    _failed.Remove(file);
                    break;
                case MoveOutcome.Locked:
                    _locked.Add(file);
                    break;
                case MoveOutcome.NoRoom when !_moved.Contains(file) || file.IsFragmented:
                    _noRoom.Add(file);
                    break;
                case MoveOutcome.Failed when !_moved.Contains(file):
                    _failed.Add(file);
                    break;
            }

            if (joined)
            {
                _joined.Add(file);
                _noRoom.Remove(file);
            }
        }
    }
}

public static class Ranges
{
    public static List<FreeRun> Of(IEnumerable<Extent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        var runs = new List<FreeRun>();
        foreach (Extent extent in extents.Where(e => !e.IsVirtual && e.Clusters > 0).OrderBy(e => e.Lcn))
        {
            if (runs.Count > 0 && runs[^1].End >= extent.Lcn)
            {
                runs[^1] = new FreeRun(runs[^1].Start, Math.Max(runs[^1].End, extent.End) - runs[^1].Start);
            }
            else
            {
                runs.Add(new FreeRun(extent.Lcn, extent.Clusters));
            }
        }

        return runs;
    }

    public static List<FreeRun> Subtract(IReadOnlyList<FreeRun> from, IReadOnlyList<FreeRun> remove)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(remove);
        var left = new List<FreeRun>();
        int cut = 0;
        foreach (FreeRun run in from)
        {
            long start = run.Start;
            while (cut < remove.Count && remove[cut].End <= start)
            {
                cut++;
            }

            for (int i = cut; i < remove.Count && remove[i].Start < run.End; i++)
            {
                if (remove[i].Start > start)
                {
                    left.Add(new FreeRun(start, remove[i].Start - start));
                }

                start = Math.Max(start, remove[i].End);
            }

            if (start < run.End)
            {
                left.Add(new FreeRun(start, run.End - start));
            }
        }

        return left;
    }
}
