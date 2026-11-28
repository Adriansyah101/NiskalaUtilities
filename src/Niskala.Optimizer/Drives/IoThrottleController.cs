namespace Niskala.Optimizer.Drives;

public readonly record struct IoSample(long BytesRead, long BytesWritten, int QueueDepth, TimeSpan At);

public sealed class IoThrottleController
{
    public const long BusyBytesPerSecond = 8L << 20;
    public const int BusyQueueDepth = 2;

    public static readonly TimeSpan Breather = TimeSpan.FromMilliseconds(1);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan LongestPause = TimeSpan.FromMilliseconds(800);

    private IoSample? _last;

    public TimeSpan Pause { get; private set; } = Breather;

    public bool IsUserBusy { get; private set; }

    public TimeSpan Next(IoSample? sample, long ownBytes)
    {
        if (sample is not { } now)
        {
            IsUserBusy = false;
            return Pause = Breather;
        }

        if (_last is not { } before || now.At <= before.At)
        {
            _last = now;
            return Pause;
        }

        _last = now;
        double seconds = (now.At - before.At).TotalSeconds;
        long moved = (now.BytesRead - before.BytesRead) + (now.BytesWritten - before.BytesWritten);
        double foreign = Math.Max(0, moved - (2.0 * ownBytes)) / seconds;
        IsUserBusy = foreign >= BusyBytesPerSecond || now.QueueDepth >= BusyQueueDepth;
        Pause = IsUserBusy
            ? TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, Math.Max(FirstBackoff.Ticks, Pause.Ticks * 2)))
            : TimeSpan.FromTicks(Math.Max(Breather.Ticks, Pause.Ticks / 2));
        return Pause;
    }
}
