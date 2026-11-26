using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Execution;

public sealed record UninstallRun(CompletionReason Reason, int? ExitCode, TimeSpan Elapsed, IReadOnlyList<JobEvent> Events, IReadOnlyList<RunningProcess> Tail);

public sealed class CompletionMonitor(CompletionOptions options, Func<bool> installerBusy, ILogger<CompletionMonitor> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<UninstallRun> WaitAsync(
        JobTracker job,
        IActivityProbe activity,
        IReadOnlyList<string> installRoots,
        IProgress<JobEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(activity);
        long start = Stopwatch.GetTimestamp();
        var detector = new CompletionDetector(installRoots, options);
        var events = new List<JobEvent>();
        CompletionPhase previous = CompletionPhase.Running;
        Task<bool>? pendingEvent = null;

        while (true)
        {
            var sample = new CompletionSample(_time.GetUtcNow(), job.ActiveProcesses(), installerBusy(), activity.Stamp());
            CompletionPhase phase = detector.Observe(sample);
            if (phase != previous)
            {
                UninstallLog.PhaseChanged(logger, previous, phase, sample.JobProcesses.Count);
                previous = phase;
            }

            if (phase == CompletionPhase.Completed)
            {
                var run = new UninstallRun(detector.Reason, job.RootExitCode, Stopwatch.GetElapsedTime(start), events, detector.Tail);
                UninstallLog.UninstallFinished(logger, run.Reason, run.ExitCode, (long)run.Elapsed.TotalMilliseconds, run.Tail.Count);
                return run;
            }

            pendingEvent ??= job.Events.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(pendingEvent, Task.Delay(options.PollInterval, _time, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingEvent.IsCompleted)
            {
                pendingEvent = null;
                while (job.Events.TryRead(out JobEvent? jobEvent))
                {
                    events.Add(jobEvent);
                    progress?.Report(jobEvent);
                }
            }
        }
    }
}
