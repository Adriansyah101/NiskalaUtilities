using System.Diagnostics;

namespace Niskala.Optimizer.Drives;

public sealed class DriveTrimmer(IVolumeSystem system)
{
    public const long Chunk = 1L << 30;
    public const long MinimumHeadroom = 4L << 30;
    public const int HeadroomDivisor = 10;

    public static long Headroom(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return Math.Max(MinimumHeadroom, volume.TotalBytes / HeadroomDivisor);
    }

    public static long FileLevelBytes(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        long cluster = Math.Max(1, volume.BytesPerCluster);
        long bytes = Math.Max(0, volume.FreeBytes - Headroom(volume));
        return bytes / cluster * cluster;
    }

    public TrimResult Run(DriveVolume volume, IProgress<DriveProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.Media != MediaKind.Ssd)
        {
            throw new InvalidOperationException($"{volume.Name} is not a solid-state drive; TRIM is only sent to SSDs.");
        }

        var clock = Stopwatch.StartNew();
        if (!volume.TrimSupported)
        {
            return new TrimResult(TrimOutcome.NotSupported, TrimMethod.None, 0, 0, clock.Elapsed);
        }

        long bytes = FileLevelBytes(volume);
        try
        {
            if (bytes > 0 && system.TryOpen(volume, out IVolumeSession? session) == 0 && session is not null)
            {
                using (session)
                {
                    int native = session.TrimFreeSpace(bytes, Chunk, done => Report(progress, done * 100.0 / bytes, volume), cancellationToken);
                    if (native == 0)
                    {
                        Report(progress, 100, volume);
                        return new TrimResult(TrimOutcome.Trimmed, TrimMethod.FileLevel, bytes, 0, clock.Elapsed);
                    }
                }
            }

            Report(progress, 0, volume);
            int exit = system.RetrimWithSystemTool(volume, percent => Report(progress, percent, volume), cancellationToken);
            Report(progress, 100, volume);
            return exit == 0
                ? new TrimResult(TrimOutcome.Trimmed, TrimMethod.SystemRetrim, volume.FreeBytes, 0, clock.Elapsed)
                : new TrimResult(TrimOutcome.Failed, TrimMethod.SystemRetrim, 0, exit, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new TrimResult(TrimOutcome.Cancelled, TrimMethod.None, 0, 0, clock.Elapsed);
        }
    }

    private static void Report(IProgress<DriveProgress>? progress, double percent, DriveVolume volume) =>
        progress?.Report(new DriveProgress(DrivePhase.Trimming, Math.Clamp(percent, 0, 100), volume.Name, 0, null));
}

public sealed class DriveOptimizer(IVolumeSystem system, VolumeAnalysisCache? cache = null)
{
    public const string WorkerName = "Niskala drive optimizer";

    public VolumeAnalysisCache Cache { get; } = cache ?? new VolumeAnalysisCache();

    public IReadOnlyList<DriveVolume> Volumes() => system.FixedVolumes();

    public DriveAnalysis? Cached(DriveVolume volume) => Cache.Find(volume);

    public Task<DriveAnalysis> AnalyzeAsync(DriveVolume volume, IProgress<DriveProgress>? progress, CancellationToken cancellationToken) =>
        OnWorker(
            () =>
            {
                DriveAnalysis analysis = new DriveAnalyzer(system).Analyze(volume, progress, cancellationToken);
                Cache.Store(analysis);
                return analysis;
            },
            background: false);

    public Task<DefragResult> DefragmentAsync(DriveAnalysis analysis, DefragMode mode, IReadOnlyCollection<string>? only, IProgress<DriveProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        DriveDefragmenter.Guard(analysis.Volume);
        return OnWorker(
            () =>
            {
                try
                {
                    return new DriveDefragmenter(system).Run(analysis, mode, only, progress, cancellationToken);
                }
                finally
                {
                    Cache.Touch(analysis);
                }
            },
            background: true);
    }

    public Task<TrimResult> TrimAsync(DriveVolume volume, IProgress<DriveProgress>? progress, CancellationToken cancellationToken) =>
        OnWorker(() => new DriveTrimmer(system).Run(volume, progress, cancellationToken), background: true);

    private Task<T> OnWorker<T>(Func<T> work, bool background)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            if (background)
            {
                system.EnterBackground();
            }

            try
            {
                done.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                done.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (background)
                {
                    system.LeaveBackground();
                }
            }
        })
        {
            IsBackground = true,
            Name = WorkerName,
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
        return done.Task;
    }
}
