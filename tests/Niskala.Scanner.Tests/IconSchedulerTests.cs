using System.Collections.Concurrent;
using Niskala.Scanner.Icons;

namespace Niskala.Scanner.Tests;

public sealed class IconSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_newest_request_is_loaded_first()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        using var scheduler = new IconScheduler(async (app, token) =>
        {
            if (app.Id == "busy")
            {
                await gate.Task.WaitAsync(token);
            }

            order.Enqueue(app.Id);
            return app.Id + ".png";
        }, concurrency: 1);

        Task<string?> busy = scheduler.Request(TestEntries.App("busy"));
        Task<string?> a = scheduler.Request(TestEntries.App("a"));
        Task<string?> b = scheduler.Request(TestEntries.App("b"));
        Task<string?> c = scheduler.Request(TestEntries.App("c"));
        Task<string?> again = scheduler.Request(TestEntries.App("a"));
        gate.SetResult();

        await Task.WhenAll(busy, a, b, c).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(["busy", "a", "c", "b"], order);
        Assert.Same(a, again);
        Assert.Equal("a.png", await a);
    }

    [Fact]
    public async Task No_more_than_the_limit_load_at_once()
    {
        int running = 0;
        int peak = 0;
        var guard = new Lock();
        using var scheduler = new IconScheduler(async (app, token) =>
        {
            int now = Interlocked.Increment(ref running);
            lock (guard)
            {
                peak = Math.Max(peak, now);
            }

            await Task.Delay(40, token);
            Interlocked.Decrement(ref running);
            return null;
        });

        Task<string?>[] requests = [.. Enumerable.Range(0, 12).Select(i => scheduler.Request(TestEntries.App("app" + i)))];

        await Task.WhenAll(requests).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.InRange(peak, 1, IconScheduler.DefaultConcurrency);
    }

    [Fact]
    public async Task A_recycled_row_withdraws_its_waiting_request()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new ConcurrentQueue<string>();
        using var scheduler = new IconScheduler(async (app, token) =>
        {
            await gate.Task.WaitAsync(token);
            loaded.Enqueue(app.Id);
            return null;
        }, concurrency: 1);

        Task<string?> running = scheduler.Request(TestEntries.App("running"));
        Task<string?> gone = scheduler.Request(TestEntries.App("gone"));

        Assert.True(scheduler.Withdraw("gone"));
        Assert.False(scheduler.Withdraw("running"));
        gate.SetResult();

        await running.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);
        Assert.Equal(["running"], loaded);
    }

    [Fact]
    public async Task Dispose_cancels_waiting_and_running_loads()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new IconScheduler(async (app, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout, token);
            return null;
        }, concurrency: 1);

        Task<string?> running = scheduler.Request(TestEntries.App("running"));
        Task<string?> waiting = scheduler.Request(TestEntries.App("waiting"));
        await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        scheduler.Dispose();
        scheduler.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.Request(TestEntries.App("late")));
        Assert.True(SpinWait.SpinUntil(() => scheduler.Running == 0, Timeout));
        Assert.Equal(0, scheduler.Waiting);
    }
}
