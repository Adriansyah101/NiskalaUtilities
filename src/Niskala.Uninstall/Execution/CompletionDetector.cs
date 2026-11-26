using Niskala.Core.Safety;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Execution;

public sealed record CompletionOptions
{
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan MaxSettle { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<string> BenignTailImages { get; init; } =
        ["msedge.exe", "msedgewebview2.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe"];
}

public enum CompletionPhase
{
    Running,
    WaitingForInstaller,
    Settling,
    Completed,
}

public enum CompletionReason
{
    None,
    AllProcessesExited,
    BenignTailOnly,
    SettleTimeout,
}

public sealed record CompletionSample(DateTimeOffset Time, IReadOnlyList<RunningProcess> JobProcesses, bool InstallerBusy, long ActivityStamp);

public sealed class CompletionDetector(IReadOnlyList<string> installRoots, CompletionOptions? options = null)
{
    private readonly CompletionOptions _options = options ?? new CompletionOptions();
    private DateTimeOffset _settleStart;
    private DateTimeOffset _lastActivity;
    private long _stamp;

    public CompletionPhase Phase { get; private set; }

    public CompletionReason Reason { get; private set; }

    public IReadOnlyList<RunningProcess> Tail { get; private set; } = [];

    public CompletionPhase Observe(CompletionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Phase == CompletionPhase.Completed)
        {
            return Phase;
        }

        bool empty = sample.JobProcesses.Count == 0;
        if (!empty && !sample.JobProcesses.All(IsBenign))
        {
            return Phase = CompletionPhase.Running;
        }

        if (sample.InstallerBusy)
        {
            return Phase = CompletionPhase.WaitingForInstaller;
        }

        if (Phase != CompletionPhase.Settling)
        {
            _settleStart = sample.Time;
            _lastActivity = sample.Time;
            _stamp = sample.ActivityStamp;
            Phase = CompletionPhase.Settling;
        }
        else if (sample.ActivityStamp != _stamp)
        {
            _stamp = sample.ActivityStamp;
            _lastActivity = sample.Time;
        }

        if (sample.Time - _lastActivity >= _options.QuietWindow)
        {
            Complete(empty ? CompletionReason.AllProcessesExited : CompletionReason.BenignTailOnly, sample);
        }
        else if (sample.Time - _settleStart >= _options.MaxSettle)
        {
            Complete(CompletionReason.SettleTimeout, sample);
        }

        return Phase;
    }

    private void Complete(CompletionReason reason, CompletionSample sample)
    {
        Phase = CompletionPhase.Completed;
        Reason = reason;
        Tail = sample.JobProcesses;
    }

    private bool IsBenign(RunningProcess process) =>
        process.ImagePath is { } image
        && _options.BenignTailImages.Contains(Path.GetFileName(image), StringComparer.OrdinalIgnoreCase)
        && !(PathSyntax.TryNormalize(image, out string normalized) == PathVerdict.Allowed
             && installRoots.Any(root => PathSyntax.IsSameOrUnder(normalized, root, out _)));
}
