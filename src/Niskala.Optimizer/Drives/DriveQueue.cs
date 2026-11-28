using System.Diagnostics;

namespace Niskala.Optimizer.Drives;

public enum QueueKind
{
    Analyze,
    Optimize,
}

public enum QueueStepKind
{
    Analyze,
    Defragment,
    Trim,
    Skip,
}

public enum QueueSkipReason
{
    None,
    UnknownMedia,
    UnsupportedFileSystem,
    NoTrim,
}

public enum QueueOutcome
{
    Done,
    Warning,
    Failed,
    Skipped,
    Cancelled,
    Refused,
}

public sealed record QueueStep(DriveVolume Volume, QueueStepKind Kind, DefragMode Mode = DefragMode.Quick, QueueSkipReason Skip = QueueSkipReason.None);

public sealed record QueueResult(
    QueueStep Step,
    QueueOutcome Outcome,
    DriveAnalysis? Analysis = null,
    DefragResult? Defrag = null,
    TrimResult? Trim = null,
    string? Error = null,
    TimeSpan Elapsed = default);

public readonly record struct QueueProgress(int Index, int Count, QueueStep Step, DriveProgress Drive);

public static class DriveQueue
{
    public static IReadOnlyList<QueueStep> Plan(IEnumerable<DriveVolume> volumes, QueueKind kind, DefragMode mode)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        return
        [
            .. volumes
                .DistinctBy(v => char.ToUpperInvariant(v.Letter))
                .OrderBy(v => char.ToUpperInvariant(v.Letter))
                .Select(v => kind == QueueKind.Analyze ? new QueueStep(v, QueueStepKind.Analyze) : Optimize(v, mode)),
        ];
    }

    public static QueueStep Optimize(DriveVolume volume, DefragMode mode)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return volume.Media switch
        {
            MediaKind.Ssd when volume.TrimSupported => new QueueStep(volume, QueueStepKind.Trim),
            MediaKind.Ssd => new QueueStep(volume, QueueStepKind.Skip, Skip: QueueSkipReason.NoTrim),
            MediaKind.Hdd when volume.SupportsMoves => new QueueStep(volume, QueueStepKind.Defragment, mode),
            MediaKind.Hdd => new QueueStep(volume, QueueStepKind.Skip, Skip: QueueSkipReason.UnsupportedFileSystem),
            _ => new QueueStep(volume, QueueStepKind.Skip, Skip: QueueSkipReason.UnknownMedia),
        };
    }

    public static bool IsAllowed(QueueStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            QueueStepKind.Defragment => step.Volume.Media == MediaKind.Hdd && step.Volume.CanRelocate,
            QueueStepKind.Trim => step.Volume.Media == MediaKind.Ssd,
            _ => true,
        };
    }
}

public sealed class DriveQueueRunner(DriveOptimizer optimizer)
{
    public async Task<IReadOnlyList<QueueResult>> RunAsync(IReadOnlyList<QueueStep> steps, IProgress<QueueProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var results = new List<QueueResult>(steps.Count);
        for (int index = 0; index < steps.Count; index++)
        {
            QueueStep step = steps[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new QueueResult(step, QueueOutcome.Cancelled));
                continue;
            }

            var clock = Stopwatch.StartNew();
            var sink = new Relay(progress, index, steps.Count, step);
            sink.Report(new DriveProgress(DrivePhase.Opening, 0, step.Volume.Name, 0, null));
            try
            {
                results.Add(await RunStepAsync(step, sink, cancellationToken).ConfigureAwait(false) with { Elapsed = clock.Elapsed });
            }
            catch (OperationCanceledException)
            {
                results.Add(new QueueResult(step, QueueOutcome.Cancelled, Elapsed: clock.Elapsed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                results.Add(new QueueResult(step, QueueOutcome.Failed, Error: ex.Message, Elapsed: clock.Elapsed));
            }
        }

        return results;
    }

    private async Task<QueueResult> RunStepAsync(QueueStep step, IProgress<DriveProgress> sink, CancellationToken cancellationToken)
    {
        if (!DriveQueue.IsAllowed(step))
        {
            return new QueueResult(step, QueueOutcome.Refused);
        }

        switch (step.Kind)
        {
            case QueueStepKind.Analyze:
                return new QueueResult(step, QueueOutcome.Done, await optimizer.AnalyzeAsync(step.Volume, sink, cancellationToken).ConfigureAwait(false));
            case QueueStepKind.Defragment:
                DriveAnalysis analysis = optimizer.Cached(step.Volume) ?? await optimizer.AnalyzeAsync(step.Volume, sink, cancellationToken).ConfigureAwait(false);
                DefragResult defrag = await optimizer.DefragmentAsync(analysis, step.Mode, null, sink, cancellationToken).ConfigureAwait(false);
                QueueOutcome moved = defrag.Cancelled ? QueueOutcome.Cancelled : defrag.Failed > 0 ? QueueOutcome.Warning : QueueOutcome.Done;
                return new QueueResult(step, moved, analysis, defrag);
            case QueueStepKind.Trim:
                TrimResult trim = await optimizer.TrimAsync(step.Volume, sink, cancellationToken).ConfigureAwait(false);
                QueueOutcome trimmed = trim.Outcome switch
                {
                    TrimOutcome.Trimmed => QueueOutcome.Done,
                    TrimOutcome.Cancelled => QueueOutcome.Cancelled,
                    TrimOutcome.Failed => QueueOutcome.Failed,
                    _ => QueueOutcome.Warning,
                };
                return new QueueResult(step, trimmed, Trim: trim);
            default:
                return new QueueResult(step, QueueOutcome.Skipped);
        }
    }

    private sealed class Relay(IProgress<QueueProgress>? progress, int index, int count, QueueStep step) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => progress?.Report(new QueueProgress(index, count, step, value));
    }
}
