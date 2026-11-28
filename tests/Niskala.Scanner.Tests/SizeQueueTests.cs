using System.Collections.Concurrent;
using Niskala.Scanner.Sizing;

namespace Niskala.Scanner.Tests;

public sealed class SizeQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Prioritized_rows_are_measured_first_newest_first()
    {
        using var busy = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        using var done = new CountdownEvent(4);
        using var queue = new SizeQueue(_ => new VolumeInfo("hdd", DiskKind.Hdd), (request, kind, _) =>
        {
            if (request.Id == "a")
            {
                busy.Set();
                gate.Wait(Timeout);
            }

            return Result(request, kind);
        });
        queue.Measured += size =>
        {
            order.Enqueue(size.Id);
            done.Signal();
        };

        foreach (string id in new[] { "a", "b", "c", "d", "c" })
        {
            queue.Enqueue(new SizeRequest(id, @"D:\" + id));
        }

        Assert.True(busy.Wait(Timeout, TestContext.Current.CancellationToken));
        queue.Prioritize("c");
        queue.Prioritize("d");
        gate.Set();

        Assert.True(done.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(["a", "d", "c", "b"], order);
    }

    [Fact]
    public void Each_volume_gets_its_own_workers()
    {
        int running = 0;
        int peak = 0;
        int expected = DiskKindProbe.WorkersFor(DiskKind.Ssd) + DiskKindProbe.WorkersFor(DiskKind.Hdd);
        using var full = new ManualResetEventSlim();
        using var done = new CountdownEvent(8);
        using var queue = new SizeQueue(
            path => path.StartsWith('S') ? new VolumeInfo("ssd", DiskKind.Ssd) : new VolumeInfo("hdd", DiskKind.Hdd),
            (request, kind, _) =>
            {
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                if (now >= expected)
                {
                    full.Set();
                }

                full.Wait(Timeout);
                Interlocked.Decrement(ref running);
                return Result(request, kind);
            });
        queue.Measured += _ => done.Signal();

        for (int i = 0; i < 6; i++)
        {
            queue.Enqueue(new SizeRequest($"s{i}", $@"S:\{i}"));
        }

        queue.Enqueue(new SizeRequest("h0", @"H:\0"));
        queue.Enqueue(new SizeRequest("h1", @"H:\1"));

        Assert.True(done.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(expected, peak);
    }

    private static FolderSize Result(SizeRequest request, DiskKind kind) =>
        new(request.Id, request.Path, true, false, 1, 1, 0, kind, TimeSpan.Zero);

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
