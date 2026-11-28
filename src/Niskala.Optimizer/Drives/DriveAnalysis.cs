using System.Diagnostics;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public sealed class DriveFile
{
    internal DriveFile(string folder, string name, long size, Extent[] extents, SpeedZone zone)
    {
        Folder = folder;
        Name = name;
        Size = size;
        Zone = zone;
        Extents = extents;
    }

    public string Folder { get; }

    public string Name { get; }

    public string Path => System.IO.Path.Join(Folder, Name);

    public long Size { get; }

    public SpeedZone Zone { get; }

    public Extent[] Extents { get; internal set; }

    public bool IsLocked { get; internal set; }

    public long Clusters => Extents.Where(e => !e.IsVirtual).Sum(e => e.Clusters);

    public long StartLcn => Extents.FirstOrDefault(e => !e.IsVirtual).Lcn;

    public int Fragments => CountFragments(Extents);

    public bool IsFragmented => Fragments > 1;

    public bool IsMovable => Extents.Length > 0 && Extents[0].Vcn == 0 && Extents.All(e => !e.IsVirtual) && IsContiguousInFile(Extents);

    public ClusterState State => IsFragmented ? ClusterState.Fragmented : Zone == SpeedZone.Archive ? ClusterState.Archive : ClusterState.Used;

    public static int CountFragments(IReadOnlyList<Extent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        int fragments = 0;
        long previousEnd = long.MinValue;
        foreach (Extent extent in extents)
        {
            if (extent.IsVirtual || extent.Clusters <= 0)
            {
                continue;
            }

            if (extent.Lcn != previousEnd)
            {
                fragments++;
            }

            previousEnd = extent.End;
        }

        return fragments;
    }

    private static bool IsContiguousInFile(Extent[] extents)
    {
        for (int i = 1; i < extents.Length; i++)
        {
            if (extents[i].Vcn != extents[i - 1].Vcn + extents[i - 1].Clusters)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record DriveSummary(
    long TotalClusters,
    long FreeClusters,
    int Files,
    int FragmentedFiles,
    long ExcessFragments,
    double FragmentedPercent,
    long LargestFreeClusters,
    int FastFiles,
    int StandardFiles,
    int ArchiveFiles,
    int Protected,
    int Locked,
    int Inaccessible);

public sealed class DriveAnalysis
{
    internal DriveAnalysis(DriveVolume volume, ClusterBitmap bitmap, DriveMap map, List<DriveFile> files, ScanCounts counts)
    {
        Volume = volume;
        Bitmap = bitmap;
        Free = FreeSpaceIndex.From(bitmap);
        Map = map;
        Files = files;
        Counts = counts;
        AnalyzedAt = DateTimeOffset.Now;
        Layout = ZoneLayout.For(
            bitmap.Clusters,
            files.Where(f => f.Zone == SpeedZone.Fast).Sum(f => f.Clusters),
            files.Where(f => f.Zone == SpeedZone.Archive).Sum(f => f.Clusters));
    }

    public DriveVolume Volume { get; }

    public ClusterBitmap Bitmap { get; }

    public FreeSpaceIndex Free { get; }

    public DriveMap Map { get; }

    public IReadOnlyList<DriveFile> Files { get; }

    public ZoneLayout Layout { get; }

    public DateTimeOffset AnalyzedAt { get; }

    internal ScanCounts Counts { get; }

    public IEnumerable<DriveFile> Fragmented => Files.Where(f => f.IsFragmented);

    public DriveSummary Summary()
    {
        long clusters = 0, fragmentedClusters = 0, excess = 0;
        int fragmented = 0, fast = 0, standard = 0, archive = 0, locked = Counts.Locked;
        foreach (DriveFile file in Files)
        {
            long size = file.Clusters;
            clusters += size;
            int fragments = file.Fragments;
            if (fragments > 1)
            {
                fragmented++;
                fragmentedClusters += size;
                excess += fragments - 1;
            }

            if (file.IsLocked)
            {
                locked++;
            }

            switch (file.Zone)
            {
                case SpeedZone.Fast:
                    fast++;
                    break;
                case SpeedZone.Archive:
                    archive++;
                    break;
                default:
                    standard++;
                    break;
            }
        }

        return new DriveSummary(
            Bitmap.Clusters,
            Free.TotalFree,
            Files.Count,
            fragmented,
            excess,
            clusters == 0 ? 0 : fragmentedClusters * 100.0 / clusters,
            Free.Largest.Length,
            fast,
            standard,
            archive,
            Counts.Protected,
            locked,
            Counts.Inaccessible);
    }
}

internal sealed record ScanCounts(int Protected, int Locked, int Inaccessible, int Links);

public static class ScanProgress
{
    public const double Ceiling = 99;

    public static double Percent(long clustersScanned, long clustersUsed, long entriesSeen, long entriesExpected)
    {
        double clusters = Math.Clamp(clustersScanned / (double)Math.Max(1, clustersUsed), 0, 1);
        if (entriesExpected <= 0)
        {
            return Math.Min(Ceiling, clusters * 100);
        }

        double entries = Math.Clamp(entriesSeen / (double)entriesExpected, 0, 1);
        return Math.Min(Ceiling, (clusters + entries) * 50);
    }
}

public sealed class DriveAnalyzer(IVolumeSystem system, TimeProvider? clock = null, Func<IoThrottleController>? throttle = null)
{
    public const int YieldEvery = 256;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(150);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<IoThrottleController> _throttle = throttle ?? (() => new IoThrottleController());

    public DriveAnalysis Analyze(DriveVolume volume, IProgress<DriveProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        progress?.Report(new DriveProgress(DrivePhase.Opening, 0, volume.Name, 0, null));
        int error = system.TryOpen(volume, out IVolumeSession? opened);
        if (error != 0 || opened is null)
        {
            throw new DriveAccessException(volume, error);
        }

        using IVolumeSession session = opened;
        progress?.Report(new DriveProgress(DrivePhase.ReadingMap, 0, volume.Name, 0, null));
        ClusterBitmap bitmap = session.ReadBitmap();
        DriveMap map = DriveMap.From(bitmap);
        progress?.Report(new DriveProgress(DrivePhase.Scanning, 0, volume.Name, map.Version, map));
        long used = Math.Max(1, bitmap.CountAllocated(0, bitmap.Clusters));
        long expected = session.FileRecords();
        long seen = 0;
        IoThrottleController controller = _throttle();
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        var files = new List<DriveFile>();
        var entries = new List<VolumeEntry>();
        var extents = new List<Extent>();
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((volume.Root, string.Empty));
        int protectedCount = 0, locked = 0, inaccessible = 0, links = 0;
        long scanned = 0;
        var report = Stopwatch.StartNew();
        while (pending.TryPop(out (string Full, string Relative) folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Clear();
            if (session.List(folder.Full, entries) != 0)
            {
                inaccessible++;
                continue;
            }

            foreach (VolumeEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++seen % YieldEvery == 0)
                {
                    Yield(session, controller, cancellationToken);
                }

                string relative = folder.Relative.Length == 0 ? entry.Name : $"{folder.Relative}\\{entry.Name}";
                string full = Path.Join(folder.Full, entry.Name);
                if (ProtectedFiles.IsProtected(relative))
                {
                    protectedCount++;
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    links++;
                    continue;
                }

                extents.Clear();
                error = session.ReadExtents(full, extents);
                if (entry.IsDirectory)
                {
                    if (error == 0)
                    {
                        map.Own(extents, ClusterState.Used, add: true);
                    }

                    pending.Push((full, relative));
                    continue;
                }

                if (error != 0)
                {
                    if (error is VolumeNative.ErrorSharingViolation or VolumeNative.ErrorLockViolation)
                    {
                        locked++;
                    }
                    else
                    {
                        inaccessible++;
                    }

                    continue;
                }

                if (extents.Count == 0)
                {
                    continue;
                }

                var file = new DriveFile(folder.Full, entry.Name, entry.Size, [.. extents], ZoneClassifier.Classify(entry.Name, entry.Size, entry.LastWriteUtc, now));
                files.Add(file);
                map.Own(file.Extents, file.State, add: true);
                scanned += file.Clusters;
                if (report.Elapsed >= ReportEvery)
                {
                    report.Restart();
                    progress?.Report(new DriveProgress(DrivePhase.Scanning, ScanProgress.Percent(scanned, used, seen, expected), full, map.Version, map));
                }
            }
        }

        var analysis = new DriveAnalysis(volume, bitmap, map, files, new ScanCounts(protectedCount, locked, inaccessible, links));
        progress?.Report(new DriveProgress(DrivePhase.Finished, 100, volume.Name, map.Version, map));
        return analysis;
    }

    private static void Yield(IVolumeSession session, IoThrottleController controller, CancellationToken cancellationToken)
    {
        TimeSpan pause = controller.Next(session.SampleIo(), 0);
        if (pause > IoThrottleController.Breather)
        {
            cancellationToken.WaitHandle.WaitOne(pause);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal static bool IsLock(int error) => error is VolumeNative.ErrorSharingViolation or VolumeNative.ErrorLockViolation or VolumeNative.ErrorAccessDenied;
}

public sealed class DriveAccessException(DriveVolume volume, int error)
    : IOException($"{volume.Name} could not be opened for optimization (Win32 error {error}).", error)
{
    public DriveVolume Volume { get; } = volume;

    public int Error { get; } = error;

    public bool NeedsElevation => Error == VolumeNative.ErrorAccessDenied;
}
