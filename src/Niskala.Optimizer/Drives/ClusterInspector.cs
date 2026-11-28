namespace Niskala.Optimizer.Drives;

public enum OccupantState
{
    Contiguous,
    Fragmented,
    Archive,
    SystemLocked,
}

public sealed record ClusterOccupant(string Path, long Size, int Fragments, OccupantState State, long ClustersInRange, long FirstLcn);

public sealed record ClusterInspection(long Start, long End, long FreeClusters, long UnlistedClusters, int TotalFiles, IReadOnlyList<ClusterOccupant> Files)
{
    public long Clusters => End - Start;

    public long UsedClusters => Clusters - FreeClusters;

    public bool IsEmpty => TotalFiles == 0 && UnlistedClusters == 0;
}

public static class ClusterInspector
{
    public const int MaxRows = 200;
    public const int CheckEvery = 4096;

    public static OccupantState StateOf(DriveFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.IsLocked ? OccupantState.SystemLocked : file.State switch
        {
            ClusterState.Fragmented => OccupantState.Fragmented,
            ClusterState.Archive => OccupantState.Archive,
            _ => OccupantState.Contiguous,
        };
    }

    public static ClusterInspection Inspect(DriveAnalysis analysis, long start, long end, int limit = MaxRows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        long clusters = analysis.Bitmap.Clusters;
        start = Math.Clamp(start, 0, clusters);
        end = Math.Clamp(end, start, clusters);
        var hits = new List<ClusterOccupant>();
        long owned = 0;
        int seen = 0;
        foreach (DriveFile file in analysis.Files)
        {
            if (++seen % CheckEvery == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            long here = 0;
            long first = long.MaxValue;
            foreach (Extent extent in file.Extents)
            {
                if (extent.IsVirtual)
                {
                    continue;
                }

                long from = Math.Max(extent.Lcn, start);
                long overlap = Math.Min(extent.End, end) - from;
                if (overlap > 0)
                {
                    here += overlap;
                    first = Math.Min(first, from);
                }
            }

            if (here > 0)
            {
                owned += here;
                hits.Add(new ClusterOccupant(file.Path, file.Size, file.Fragments, StateOf(file), here, first));
            }
        }

        long allocated = end > start ? analysis.Bitmap.CountAllocated(start, end - start) : 0;
        ClusterOccupant[] rows = [.. hits.OrderByDescending(h => h.ClustersInRange).ThenBy(h => h.FirstLcn).ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase).Take(limit)];
        return new ClusterInspection(start, end, end - start - allocated, Math.Max(0, allocated - owned), hits.Count, rows);
    }
}
