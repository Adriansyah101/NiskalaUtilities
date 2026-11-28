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

    public string Folder { get; internal set; }

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
    int Inaccessible,
    int ResidentFiles = 0);

public sealed class DriveAnalysis
{
    internal DriveAnalysis(DriveVolume volume, ClusterBitmap bitmap, DriveMap map, List<DriveFile> files, ScanCounts counts, ScanMethod method)
    {
        Volume = volume;
        Method = method;
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

    public ScanMethod Method { get; }

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
            Counts.Inaccessible,
            Counts.Resident);
    }
}

internal sealed record ScanCounts(int Protected, int Locked, int Inaccessible, int Links, int Resident = 0, int Opened = 0);

public static class ScanProgress
{
    public const double Ceiling = 99;
    public const double TableShare = 25;
    public const double RecordShare = 90;

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

    public static double Table(long recordsRead, long recordsExpected, double share = TableShare) =>
        recordsExpected <= 0 ? 0 : share * Math.Clamp(recordsRead / (double)recordsExpected, 0, 1);

    public static double AfterTable(double scanPercent, double share = TableShare) =>
        Math.Min(Ceiling, share + (Math.Clamp(scanPercent, 0, 100) * (100 - share) / 100));
}

public sealed class DriveAnalyzer(IVolumeSystem system, TimeProvider? clock = null, Func<IoThrottleController>? throttle = null)
{
    public const int YieldEvery = 256;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(150);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<IoThrottleController> _throttle = throttle ?? NewThrottle;

    public static IoThrottleController NewThrottle() => new(queueOnly: true);

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
        var scan = new Scan(
            volume,
            map,
            Math.Max(1, bitmap.CountAllocated(0, bitmap.Clusters)),
            session.FileRecords(),
            _clock.GetUtcNow().UtcDateTime,
            session,
            _throttle(),
            progress);
        ScanMethod method = ReadFileRecords(session, scan, cancellationToken) is { } direct
            ? direct.Scan(session, scan, cancellationToken)
            : ReadFileTable(session, scan, cancellationToken) is { } records
            ? ScanFileTable(session, records, scan, cancellationToken)
            : WalkFolders(session, scan, cancellationToken);
        var analysis = new DriveAnalysis(volume, bitmap, map, scan.Files, scan.Counts(), method);
        progress?.Report(new DriveProgress(DrivePhase.Finished, 100, volume.Name, map.Version, map));
        return analysis;
    }

    internal static bool IsLock(int error) => error is VolumeNative.ErrorSharingViolation or VolumeNative.ErrorLockViolation or VolumeNative.ErrorAccessDenied;

    private static RecordSet? ReadFileRecords(IVolumeSession session, Scan scan, CancellationToken cancellationToken)
    {
        if (!FileTableScan.Supports(scan.Volume))
        {
            return null;
        }

        scan.Report(DrivePhase.ReadingFileTable, 0, scan.Volume.Name);
        var records = new RecordSet(scan);
        var report = Stopwatch.StartNew();
        int error = session.ReadFileRecords(
            records.Add,
            count =>
            {
                scan.Breathe(cancellationToken);
                if (report.Elapsed >= ReportEvery)
                {
                    report.Restart();
                    scan.Report(DrivePhase.ReadingFileTable, ScanProgress.Table(count, scan.Expected, ScanProgress.RecordShare), scan.Volume.Name);
                }
            },
            cancellationToken,
            out int damaged);
        if (error == 0 && records.Count > 0)
        {
            scan.Inaccessible += damaged;
            return records;
        }

        return null;
    }

    private static List<MftRecord>? ReadFileTable(IVolumeSession session, Scan scan, CancellationToken cancellationToken)
    {
        if (!FileTableScan.Supports(scan.Volume))
        {
            return null;
        }

        scan.Report(DrivePhase.ReadingFileTable, 0, scan.Volume.Name);
        var records = new List<MftRecord>();
        var report = Stopwatch.StartNew();
        int error = session.ReadFileTable(
            records,
            count =>
            {
                if (report.Elapsed >= ReportEvery)
                {
                    report.Restart();
                    scan.Report(DrivePhase.ReadingFileTable, ScanProgress.Table(count, scan.Expected), scan.Volume.Name);
                }
            },
            cancellationToken);
        if (error == 0 && records.Count > 0)
        {
            return records;
        }

        records.Clear();
        return null;
    }

    private static ScanMethod ScanFileTable(IVolumeSession session, List<MftRecord> records, Scan scan, CancellationToken cancellationToken)
    {
        FileTablePaths paths = FileTablePaths.From(scan.Volume.Root, records);
        FileTableScan.SortByRecord(records);
        scan.StartTable(records.Count, ScanProgress.TableShare);
        var extents = new List<Extent>();
        long previous = -1;
        foreach (MftRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scan.Seen(cancellationToken);
            if (Admit(record, paths, scan, ref previous) is { } folder)
            {
                Open(session, scan, record, folder, extents);
            }
        }

        records.Clear();
        records.TrimExcess();
        return ScanMethod.FileTable;
    }

    private static TableFolder? Admit(MftRecord record, FileTablePaths paths, Scan scan, ref long previous)
    {
        if (record.Id == previous)
        {
            return null;
        }

        previous = record.Id;
        if (record.Number < FileTableScan.FirstUserRecord)
        {
            if (record.Number != FileTableScan.RootRecord)
            {
                scan.Protected++;
            }

            return null;
        }

        if (paths.Resolve(record.Parent) is not { } folder || folder.IsProtected || ProtectedFiles.IsProtectedName(record.Name))
        {
            scan.Protected++;
            return null;
        }

        if (record.IsReparsePoint)
        {
            scan.Links++;
            return null;
        }

        return folder;
    }

    private static void Open(IVolumeSession session, Scan scan, MftRecord record, TableFolder folder, List<Extent> extents)
    {
        extents.Clear();
        scan.Opened++;
        int error = session.ReadRecord(record.Id, extents, out RecordFacts facts);
        if (record.IsDirectory)
        {
            scan.Folder(error, extents);
            return;
        }

        scan.Add(folder.Full, record.Name, error, facts.Size, facts.LastWriteUtc, extents);
    }

    private static ScanMethod WalkFolders(IVolumeSession session, Scan scan, CancellationToken cancellationToken)
    {
        scan.Report(DrivePhase.Scanning, 0, scan.Volume.Name);
        var entries = new List<VolumeEntry>();
        var extents = new List<Extent>();
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((scan.Volume.Root, string.Empty));
        while (pending.TryPop(out (string Full, string Relative) folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Clear();
            if (session.List(folder.Full, entries) != 0)
            {
                scan.Inaccessible++;
                continue;
            }

            foreach (VolumeEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.Seen(cancellationToken);
                string relative = folder.Relative.Length == 0 ? entry.Name : $"{folder.Relative}\\{entry.Name}";
                string full = Path.Join(folder.Full, entry.Name);
                if (ProtectedFiles.IsProtected(relative))
                {
                    scan.Protected++;
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    scan.Links++;
                    continue;
                }

                extents.Clear();
                int error = session.ReadExtents(full, extents);
                if (entry.IsDirectory)
                {
                    scan.Folder(error, extents);
                    pending.Push((full, relative));
                    continue;
                }

                scan.Add(folder.Full, entry.Name, error, entry.Size, entry.LastWriteUtc, extents);
            }
        }

        return ScanMethod.Folders;
    }

    private sealed class RecordSet(Scan scan)
    {
        private readonly List<FileRecord> _folders = [];
        private readonly List<FileRecord> _unknown = [];
        private readonly List<(long Parent, DriveFile File)> _files = [];
        private readonly List<(long Parent, ContentKind Kind)> _small = [];
        private int _system;

        private enum ContentKind : byte
        {
            Resident,
            Link,
            ProtectedName,
        }

        public int Count => _folders.Count + _unknown.Count + _files.Count + _small.Count + _system;

        public void Add(FileRecord record)
        {
            MftRecord entry = record.Entry;
            if (entry.IsDirectory)
            {
                _folders.Add(record);
                return;
            }

            if (entry.Number < FileTableScan.FirstUserRecord)
            {
                _system++;
                return;
            }

            if (ProtectedFiles.IsProtectedName(entry.Name))
            {
                _small.Add((entry.Parent, ContentKind.ProtectedName));
            }
            else if (entry.IsReparsePoint)
            {
                _small.Add((entry.Parent, ContentKind.Link));
            }
            else if (record.Extents is not { } extents)
            {
                _unknown.Add(record);
            }
            else if (extents.Length == 0)
            {
                _small.Add((entry.Parent, ContentKind.Resident));
            }
            else
            {
                _files.Add((entry.Parent, scan.Create(entry.Name, record.Size, record.LastWriteUtc, extents)));
            }
        }

        public ScanMethod Scan(IVolumeSession session, Scan scan, CancellationToken cancellationToken)
        {
            var paths = new FileTablePaths(scan.Volume.Root);
            foreach (FileRecord folder in _folders)
            {
                if (!folder.Entry.IsReparsePoint)
                {
                    paths.AddFolder(folder.Entry.Id, folder.Entry.Parent, folder.Entry.Name);
                }
            }

            scan.StartTable(Count, ScanProgress.RecordShare);
            scan.Protected += _system;
            var extents = new List<Extent>();
            long previous = -1;
            foreach (FileRecord folder in _folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.Seen(cancellationToken);
                if (Admit(folder.Entry, paths, scan, ref previous) is { } at)
                {
                    if (folder.Extents is { } known)
                    {
                        scan.Folder(0, known);
                    }
                    else
                    {
                        Open(session, scan, folder.Entry, at, extents);
                    }
                }
            }

            foreach ((long parent, DriveFile file) in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.Seen(cancellationToken);
                if (paths.Resolve(parent) is not { IsProtected: false } at)
                {
                    scan.Protected++;
                    continue;
                }

                file.Folder = at.Full;
                scan.Adopt(file);
            }

            foreach ((long parent, ContentKind kind) in _small)
            {
                scan.Seen(cancellationToken);
                if (kind == ContentKind.ProtectedName || paths.Resolve(parent) is not { IsProtected: false })
                {
                    scan.Protected++;
                }
                else if (kind == ContentKind.Link)
                {
                    scan.Links++;
                }
                else
                {
                    scan.Resident++;
                }
            }

            foreach (FileRecord record in _unknown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.Seen(cancellationToken);
                if (Admit(record.Entry, paths, scan, ref previous) is { } at)
                {
                    Open(session, scan, record.Entry, at, extents);
                }
            }

            _folders.Clear();
            _files.Clear();
            _small.Clear();
            _unknown.Clear();
            return ScanMethod.FileRecords;
        }
    }

    private sealed class Scan(
        DriveVolume volume,
        DriveMap map,
        long used,
        long expected,
        DateTime now,
        IVolumeSession session,
        IoThrottleController controller,
        IProgress<DriveProgress>? progress)
    {
        private readonly Stopwatch _report = Stopwatch.StartNew();
        private long _seen;
        private long _scanned;
        private long _entries = expected;
        private bool _table;
        private double _share = ScanProgress.TableShare;

        public DriveVolume Volume => volume;

        public long Expected { get; } = expected;

        public List<DriveFile> Files { get; } = [];

        public int Protected { get; set; }

        public int Locked { get; set; }

        public int Inaccessible { get; set; }

        public int Links { get; set; }

        public int Resident { get; set; }

        public int Opened { get; set; }

        public ScanCounts Counts() => new(Protected, Locked, Inaccessible, Links, Resident, Opened);

        public void StartTable(long records, double share)
        {
            _table = true;
            _share = share;
            _entries = records;
            Report(DrivePhase.Scanning, ScanProgress.AfterTable(0, share), volume.Name);
        }

        public void Report(DrivePhase phase, double percent, string current) =>
            progress?.Report(new DriveProgress(phase, percent, current, map.Version, map));

        public void Seen(CancellationToken cancellationToken)
        {
            if (++_seen % YieldEvery == 0)
            {
                Breathe(cancellationToken);
            }
        }

        public void Breathe(CancellationToken cancellationToken)
        {
            TimeSpan pause = controller.Next(session.SampleIo(), 0);
            if (pause > IoThrottleController.Breather)
            {
                cancellationToken.WaitHandle.WaitOne(pause);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void Folder(int error, IEnumerable<Extent> extents)
        {
            if (error == 0)
            {
                map.Own(extents, ClusterState.Used, add: true);
            }
        }

        public void Add(string folder, string name, int error, long size, DateTime lastWriteUtc, List<Extent> extents)
        {
            if (error != 0)
            {
                if (error is VolumeNative.ErrorSharingViolation or VolumeNative.ErrorLockViolation)
                {
                    Locked++;
                }
                else
                {
                    Inaccessible++;
                }

                return;
            }

            if (extents.Count == 0)
            {
                Resident++;
                return;
            }

            DriveFile file = Create(name, size, lastWriteUtc, [.. extents]);
            file.Folder = folder;
            Adopt(file);
        }

        public DriveFile Create(string name, long size, DateTime lastWriteUtc, Extent[] extents) =>
            new(string.Empty, name, size, extents, ZoneClassifier.Classify(name, size, lastWriteUtc, now));

        public void Adopt(DriveFile file)
        {
            Files.Add(file);
            map.Own(file.Extents, file.State, add: true);
            _scanned += file.Clusters;
            if (_report.Elapsed >= ReportEvery)
            {
                _report.Restart();
                double percent = ScanProgress.Percent(_scanned, used, _seen, _entries);
                Report(DrivePhase.Scanning, _table ? ScanProgress.AfterTable(percent, _share) : percent, file.Path);
            }
        }
    }
}

public sealed class DriveAccessException(DriveVolume volume, int error)
    : IOException($"{volume.Name} could not be opened for optimization (Win32 error {error}).", error)
{
    public DriveVolume Volume { get; } = volume;

    public int Error { get; } = error;

    public bool NeedsElevation => Error == VolumeNative.ErrorAccessDenied;
}
