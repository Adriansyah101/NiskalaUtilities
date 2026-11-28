namespace Niskala.Optimizer.Drives;

public sealed class FreeSpaceIndex
{
    private readonly SortedSet<FreeRun> _byStart = new(Comparer<FreeRun>.Create((a, b) => a.Start.CompareTo(b.Start)));
    private readonly SortedSet<FreeRun> _bySize = new(Comparer<FreeRun>.Create((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.Start.CompareTo(b.Start)));

    public FreeSpaceIndex(IEnumerable<FreeRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        foreach (FreeRun run in runs)
        {
            if (run.Length > 0)
            {
                Release(run.Start, run.Length);
            }
        }
    }

    public int Count => _byStart.Count;

    public long TotalFree { get; private set; }

    public FreeRun Largest => _bySize.Count > 0 ? _bySize.Max : default;

    public IEnumerable<FreeRun> Runs => _byStart;

    public static FreeSpaceIndex From(ClusterBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new FreeSpaceIndex(bitmap.FreeRuns());
    }

    public long? BestFit(long clusters, long from = 0, long to = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusters);
        foreach (FreeRun run in _bySize.GetViewBetween(new FreeRun(long.MinValue, clusters), new FreeRun(long.MaxValue, long.MaxValue)))
        {
            long start = Math.Max(run.Start, from);
            if (Math.Min(run.End, to) - start >= clusters)
            {
                return start;
            }
        }

        return null;
    }

    public long? FirstFit(long clusters, long from = 0, long to = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusters);
        if (Containing(from) is { } first && Math.Min(first.End, to) - from >= clusters)
        {
            return from;
        }

        foreach (FreeRun run in _byStart.GetViewBetween(new FreeRun(from, 0), new FreeRun(long.MaxValue, 0)))
        {
            if (run.Start >= to)
            {
                break;
            }

            if (Math.Min(run.End, to) - run.Start >= clusters)
            {
                return run.Start;
            }
        }

        return null;
    }

    public bool IsFree(long start, long clusters) => Containing(start) is { } run && run.End >= start + clusters;

    public void Allocate(long start, long clusters)
    {
        if (clusters <= 0)
        {
            return;
        }

        long end = start + clusters;
        foreach (FreeRun run in Overlapping(start, end))
        {
            Remove(run);
            if (run.Start < start)
            {
                Add(new FreeRun(run.Start, start - run.Start));
            }

            if (run.End > end)
            {
                Add(new FreeRun(end, run.End - end));
            }
        }
    }

    public void Release(long start, long clusters)
    {
        if (clusters <= 0)
        {
            return;
        }

        long end = start + clusters;
        foreach (FreeRun run in Overlapping(start - 1, end + 1))
        {
            Remove(run);
            start = Math.Min(start, run.Start);
            end = Math.Max(end, run.End);
        }

        Add(new FreeRun(start, end - start));
    }

    private FreeRun? Containing(long lcn)
    {
        FreeRun run = _byStart.GetViewBetween(new FreeRun(long.MinValue, 0), new FreeRun(lcn, 0)).Max;
        return run.Length > 0 && run.Start <= lcn && run.End > lcn ? run : null;
    }

    private List<FreeRun> Overlapping(long start, long end)
    {
        var found = new List<FreeRun>();
        if (Containing(start) is { } head)
        {
            found.Add(head);
        }

        foreach (FreeRun run in _byStart.GetViewBetween(new FreeRun(start, 0), new FreeRun(end, 0)))
        {
            if (run.Start < end && (found.Count == 0 || found[^1] != run))
            {
                found.Add(run);
            }
        }

        return found;
    }

    private void Add(FreeRun run)
    {
        _byStart.Add(run);
        _bySize.Add(run);
        TotalFree += run.Length;
    }

    private void Remove(FreeRun run)
    {
        _byStart.Remove(run);
        _bySize.Remove(run);
        TotalFree -= run.Length;
    }
}
