namespace Niskala.Optimizer.Drives;

public sealed class DriveMap
{
    public const int DefaultSegments = 8192;

    private const int FragmentedWeight = 3;

    private readonly object _gate = new();
    private readonly long[] _allocated;
    private readonly long[] _owned;
    private readonly long[] _fragmented;
    private readonly long[] _archive;
    private readonly long[] _reserved;
    private readonly long[] _compressed;
    private long _version;

    public DriveMap(long clusters, int segments = DefaultSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segments);
        Clusters = clusters;
        Segments = (int)Math.Max(1, Math.Min(segments, clusters));
        _allocated = new long[Segments];
        _owned = new long[Segments];
        _fragmented = new long[Segments];
        _archive = new long[Segments];
        _reserved = new long[Segments];
        _compressed = new long[Segments];
    }

    public long Clusters { get; }

    public int Segments { get; }

    public long Version => Interlocked.Read(ref _version);

    public static DriveMap From(ClusterBitmap bitmap, int segments = DefaultSegments)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var map = new DriveMap(bitmap.Clusters, segments);
        for (int segment = 0; segment < map.Segments; segment++)
        {
            (long start, long end) = map.Bounds(segment);
            map._allocated[segment] = bitmap.CountAllocated(start, end - start);
        }

        return map;
    }

    public void Allocate(long start, long clusters, bool allocated) => Apply(_allocated, start, clusters, allocated ? 1 : -1);

    public void Own(IEnumerable<Extent> extents, ClusterState state, bool add)
    {
        ArgumentNullException.ThrowIfNull(extents);
        int sign = add ? 1 : -1;
        lock (_gate)
        {
            foreach (Extent extent in extents)
            {
                if (extent.IsVirtual)
                {
                    continue;
                }

                Spread(_owned, extent.Lcn, extent.Clusters, sign);
                if (state == ClusterState.Fragmented)
                {
                    Spread(_fragmented, extent.Lcn, extent.Clusters, sign);
                }
                else if (state == ClusterState.Archive)
                {
                    Spread(_archive, extent.Lcn, extent.Clusters, sign);
                }
                else if (state == ClusterState.Compressed)
                {
                    Spread(_compressed, extent.Lcn, extent.Clusters, sign);
                }
            }

            _version++;
        }
    }

    public void Reserve(IEnumerable<FreeRun> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        lock (_gate)
        {
            foreach (FreeRun range in ranges)
            {
                Spread(_reserved, range.Start, range.Length, 1);
            }

            _version++;
        }
    }

    public ClusterState[] Summarize(int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        var states = new ClusterState[cells];
        lock (_gate)
        {
            for (int cell = 0; cell < cells; cell++)
            {
                (int first, int last) = CellSegments(cell, cells);
                long total = 0, allocated = 0, owned = 0, fragmented = 0, archive = 0, reserved = 0, compressed = 0;
                for (int segment = first; segment < last && segment < Segments; segment++)
                {
                    (long start, long end) = Bounds(segment);
                    total += end - start;
                    allocated += _allocated[segment];
                    owned += _owned[segment];
                    fragmented += _fragmented[segment];
                    archive += _archive[segment];
                    reserved += _reserved[segment];
                    compressed += _compressed[segment];
                }

                states[cell] = Pick(total, allocated, owned, fragmented, archive, reserved, compressed);
            }
        }

        return states;
    }

    public (long Start, long End) CellRange(int cell, int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, cells);
        (int first, int last) = CellSegments(cell, cells);
        return (Bounds(first).Start, Bounds(Math.Min(last, Segments) - 1).End);
    }

    public int CellOf(long lcn, int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        int segment = SegmentOf(Math.Clamp(lcn, 0, Math.Max(0, Clusters - 1)));
        int cell = (int)Math.Min(cells - 1, (long)segment * cells / Segments);
        while (cell > 0 && CellSegments(cell, cells).First > segment)
        {
            cell--;
        }

        while (cell < cells - 1 && CellSegments(cell, cells).Last <= segment)
        {
            cell++;
        }

        return cell;
    }

    public (long Free, long Used, long Fragmented, long Archive, long System) Totals()
    {
        lock (_gate)
        {
            long allocated = _allocated.Sum(), owned = _owned.Sum(), fragmented = _fragmented.Sum(), archive = _archive.Sum();
            long ownedInUse = Math.Min(owned, allocated);
            return (Clusters - allocated, Math.Max(0, ownedInUse - fragmented - archive), fragmented, archive, Math.Max(0, allocated - ownedInUse));
        }
    }

    public (long Reserved, long Compressed) SpecialTotals()
    {
        lock (_gate)
        {
            return (_reserved.Sum(), _compressed.Sum());
        }
    }

    internal static ClusterState Pick(long total, long allocated, long owned, long fragmented, long archive, long reserved = 0, long compressed = 0)
    {
        long free = Math.Max(0, total - allocated);
        long ownedInUse = Math.Min(owned, allocated);
        long used = Math.Max(0, ownedInUse - fragmented - archive - compressed);
        long system = Math.Max(0, allocated - ownedInUse);
        long held = Math.Min(Math.Max(0, reserved), free + system);
        long fromSystem = Math.Min(system, held);
        system -= fromSystem;
        free -= held - fromSystem;
        (ClusterState State, long Score)[] scores =
        [
            (ClusterState.Free, free),
            (ClusterState.Used, used),
            (ClusterState.Fragmented, fragmented * FragmentedWeight),
            (ClusterState.Archive, archive),
            (ClusterState.System, system),
            (ClusterState.MftReserved, held),
            (ClusterState.Compressed, compressed),
        ];
        return scores.MaxBy(s => s.Score).State;
    }

    private (long Start, long End) Bounds(int segment) =>
        (segment * Clusters / Segments, (segment + 1L) * Clusters / Segments);

    private (int First, int Last) CellSegments(int cell, int cells)
    {
        int first = (int)((long)cell * Segments / cells);
        return (first, (int)Math.Max(first + 1, (long)(cell + 1) * Segments / cells));
    }

    private int SegmentOf(long lcn)
    {
        int segment = (int)Math.Min(Segments - 1, lcn * Segments / Math.Max(1, Clusters));
        while (segment < Segments - 1 && Bounds(segment).End <= lcn)
        {
            segment++;
        }

        while (segment > 0 && Bounds(segment).Start > lcn)
        {
            segment--;
        }

        return segment;
    }

    private void Apply(long[] counts, long start, long clusters, int sign)
    {
        lock (_gate)
        {
            Spread(counts, start, clusters, sign);
            _version++;
        }
    }

    private void Spread(long[] counts, long start, long clusters, int sign)
    {
        long end = Math.Min(Clusters, start + clusters);
        start = Math.Max(0, start);
        while (start < end)
        {
            int segment = (int)Math.Min(Segments - 1, start * Segments / Math.Max(1, Clusters));
            (long _, long segmentEnd) = Bounds(segment);
            while (segmentEnd <= start && segment < Segments - 1)
            {
                segment++;
                (_, segmentEnd) = Bounds(segment);
            }

            long take = Math.Min(end, segmentEnd) - start;
            counts[segment] += sign * take;
            start += take;
        }
    }
}

public static class ReservedArea
{
    public static IReadOnlyList<FreeRun> Merge(IEnumerable<FreeRun> ranges, long clusters)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var merged = new List<FreeRun>();
        foreach (FreeRun range in ranges.Where(r => r.Length > 0).OrderBy(r => r.Start))
        {
            long start = Math.Clamp(range.Start, 0, clusters);
            long end = Math.Clamp(range.End, start, clusters);
            if (end <= start)
            {
                continue;
            }

            if (merged.Count > 0 && start <= merged[^1].End)
            {
                FreeRun last = merged[^1];
                merged[^1] = new FreeRun(last.Start, Math.Max(last.End, end) - last.Start);
            }
            else
            {
                merged.Add(new FreeRun(start, end - start));
            }
        }

        return merged;
    }

    public static long Overlap(IReadOnlyList<FreeRun> ranges, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        long total = 0;
        foreach (FreeRun range in ranges)
        {
            long overlap = Math.Min(range.End, end) - Math.Max(range.Start, start);
            if (overlap > 0)
            {
                total += overlap;
            }
        }

        return total;
    }
}
