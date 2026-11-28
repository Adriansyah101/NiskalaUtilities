using System.Numerics;
using System.Runtime.InteropServices;

namespace Niskala.Optimizer.Drives;

public sealed class ClusterBitmap
{
    private const int WordBits = 64;

    private readonly ulong[] _words;

    public ClusterBitmap(long clusters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusters);
        Clusters = clusters;
        _words = new ulong[(clusters + WordBits - 1) / WordBits];
    }

    private ClusterBitmap(ulong[] words, long clusters)
    {
        Clusters = clusters;
        _words = words;
    }

    public long Clusters { get; }

    public static ClusterBitmap FromBytes(ReadOnlySpan<byte> bits, long clusters)
    {
        var bitmap = new ClusterBitmap(clusters);
        int bytes = (int)Math.Min(bits.Length, (clusters + 7) / 8);
        bits[..bytes].CopyTo(MemoryMarshal.AsBytes(bitmap._words.AsSpan()));
        bitmap.ClearTail();
        return bitmap;
    }

    public static ClusterBitmap FromWords(ulong[] words, long clusters)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfNegative(clusters);
        ArgumentOutOfRangeException.ThrowIfLessThan(words.LongLength, (clusters + WordBits - 1) / WordBits);
        var bitmap = new ClusterBitmap(words, clusters);
        bitmap.ClearTail();
        return bitmap;
    }

    public bool IsAllocated(long lcn) => lcn >= 0 && lcn < Clusters && (_words[lcn / WordBits] & (1UL << (int)(lcn % WordBits))) != 0;

    public void Set(long start, long count, bool allocated)
    {
        Check(start, count);
        long end = start + count;
        while (start < end)
        {
            int bit = (int)(start % WordBits);
            int take = (int)Math.Min(WordBits - bit, end - start);
            ulong mask = take == WordBits ? ulong.MaxValue : ((1UL << take) - 1) << bit;
            if (allocated)
            {
                _words[start / WordBits] |= mask;
            }
            else
            {
                _words[start / WordBits] &= ~mask;
            }

            start += take;
        }
    }

    public long CountAllocated(long start, long count)
    {
        Check(start, count);
        long end = start + count;
        long total = 0;
        while (start < end)
        {
            int bit = (int)(start % WordBits);
            int take = (int)Math.Min(WordBits - bit, end - start);
            ulong mask = take == WordBits ? ulong.MaxValue : ((1UL << take) - 1) << bit;
            total += BitOperations.PopCount(_words[start / WordBits] & mask);
            start += take;
        }

        return total;
    }

    public bool IsFree(long start, long count) => start >= 0 && start + count <= Clusters && CountAllocated(start, count) == 0;

    public IEnumerable<FreeRun> FreeRuns()
    {
        long runStart = -1;
        for (long word = 0; word < _words.Length; word++)
        {
            ulong value = _words[word];
            long baseLcn = word * WordBits;
            if (value == ulong.MaxValue)
            {
                if (runStart >= 0)
                {
                    yield return new FreeRun(runStart, baseLcn - runStart);
                    runStart = -1;
                }

                continue;
            }

            if (value == 0)
            {
                if (runStart < 0)
                {
                    runStart = baseLcn;
                }

                continue;
            }

            for (int bit = 0; bit < WordBits; bit++)
            {
                bool used = (value & (1UL << bit)) != 0;
                if (!used && runStart < 0)
                {
                    runStart = baseLcn + bit;
                }
                else if (used && runStart >= 0)
                {
                    yield return new FreeRun(runStart, baseLcn + bit - runStart);
                    runStart = -1;
                }
            }
        }

        if (runStart >= 0 && runStart < Clusters)
        {
            yield return new FreeRun(runStart, Clusters - runStart);
        }
    }

    public IEnumerable<FreeRun> FreeRuns(long start, long count)
    {
        Check(start, count);
        long runStart = -1;
        long end = start + count;
        for (long lcn = start; lcn < end; lcn++)
        {
            bool used = IsAllocated(lcn);
            if (!used && runStart < 0)
            {
                runStart = lcn;
            }
            else if (used && runStart >= 0)
            {
                yield return new FreeRun(runStart, lcn - runStart);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            yield return new FreeRun(runStart, end - runStart);
        }
    }

    private void ClearTail()
    {
        int spare = (int)(Clusters % WordBits);
        if (spare != 0)
        {
            _words[^1] &= (1UL << spare) - 1;
        }
    }

    private void Check(long start, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, Clusters);
    }
}
