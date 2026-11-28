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
                int first = (int)((long)cell * Segments / cells);
                int last = (int)Math.Max(first + 1, (long)(cell + 1) * Segments / cells);
                long total = 0, allocated = 0, owned = 0, fragmented = 0, archive = 0;
                for (int segment = first; segment < last && segment < Segments; segment++)
                {
                    (long start, long end) = Bounds(segment);
                    total += end - start;
                    allocated += _allocated[segment];
                    owned += _owned[segment];
                    fragmented += _fragmented[segment];
                    archive += _archive[segment];
                }

                states[cell] = Pick(total, allocated, owned, fragmented, archive);
            }
        }

        return states;
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

    internal static ClusterState Pick(long total, long allocated, long owned, long fragmented, long archive)
    {
        long free = Math.Max(0, total - allocated);
        long ownedInUse = Math.Min(owned, allocated);
        long used = Math.Max(0, ownedInUse - fragmented - archive);
        long system = Math.Max(0, allocated - ownedInUse);
        (ClusterState State, long Score)[] scores =
        [
            (ClusterState.Free, free),
            (ClusterState.Used, used),
            (ClusterState.Fragmented, fragmented * FragmentedWeight),
            (ClusterState.Archive, archive),
            (ClusterState.System, system),
        ];
        return scores.MaxBy(s => s.Score).State;
    }

    private (long Start, long End) Bounds(int segment) =>
        (segment * Clusters / Segments, (segment + 1L) * Clusters / Segments);

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
