namespace Niskala.UI.Services;

internal sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly SynchronizationContext _context = SynchronizationContext.Current ?? new SynchronizationContext();
    private long _last;

    public void Report(T value)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _last);
        if (now - last < Interval.TotalMilliseconds || Interlocked.CompareExchange(ref _last, now, last) != last)
        {
            return;
        }

        _context.Post(state => handler((T)state!), value);
    }
}
