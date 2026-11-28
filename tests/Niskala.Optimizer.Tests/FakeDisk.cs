using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

internal sealed class FakeDisk : IVolumeSystem
{
    private const long Unallocated = -1;
    private const long FirstRecord = 64;
    private const long Sequence = 1L << 48;

    private readonly bool[] _used;
    private readonly Dictionary<string, long[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Directory, bool Link, long Size, DateTime Written)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _paths = [];

    public FakeDisk(long clusters, MediaKind media = MediaKind.Hdd, string fileSystem = "NTFS", bool trim = true, long bytesPerCluster = 4096, char letter = 'X')
    {
        _used = new bool[clusters];
        Volume = new DriveVolume(letter, "Test", fileSystem, bytesPerCluster, clusters, clusters * bytesPerCluster, clusters * bytesPerCluster, media, trim, SerialNumber: InitialSerial);
    }

    public const uint InitialSerial = 0x5EED_0001;

    public static long RootId { get; } = FileTableScan.RootRecord | (FileTableScan.RootRecord << 48);

    public bool FileTable { get; set; }

    public int FileTableError { get; set; }

    public bool RecordsOutOfOrder { get; set; }

    public bool DirectRecords { get; set; }

    public int DirectRecordsError { get; set; }

    public int DirectReads { get; private set; }

    public HashSet<string> WithoutExtents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Compressed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FreeRun> ReservedZone { get; } = [];

    public List<(long Lcn, long Clusters)> MetadataRuns { get; } = [];

    public int FileTableReads { get; private set; }

    public List<long> RecordReads { get; } = [];

    public Action<int>? OnMove { get; set; }

    public long IdOf(string relative) => _ids[Path.Join(Root, relative)];

    public FakeDisk Reformat(uint serial)
    {
        Volume = Volume with { SerialNumber = serial };
        return this;
    }

    public FakeDisk Relocate(string relative, params (long Lcn, long Clusters)[] runs)
    {
        string path = Path.Join(Root, relative);
        foreach (long lcn in _files[path])
        {
            _used[lcn] = false;
        }

        _files.Remove(path);
        _entries.Remove(path);
        return File(relative, runs);
    }

    public DriveVolume Volume { get; private set; }

    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int RangeReads { get; private set; }

    public long Records { get; set; }

    public int Reads { get; private set; }

    public Action<int>? OnRead { get; set; }

    public int? BusyQueue { get; set; }

    private long _samples;

    public List<MoveStep> Moves { get; } = [];

    public int NativeTrimError { get; set; }

    public long NativeTrimmed { get; private set; }

    public int RetrimCalls { get; private set; }

    public int RetrimExit { get; set; }

    public int Opened { get; private set; }

    public bool InBackground { get; private set; }

    public bool OpenedInBackground { get; private set; }

    public static DateTime Now { get; } = new(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);

    public string Root => Volume.Root;

    public FakeDisk Folder(string relative)
    {
        _entries[Path.Join(Root, relative)] = (true, false, 0, Now);
        Name(relative);
        return this;
    }

    public FakeDisk Link(string relative)
    {
        _entries[Path.Join(Root, relative)] = (true, true, 0, Now);
        Name(relative);
        return this;
    }

    public FakeDisk File(string relative, params (long Lcn, long Clusters)[] runs) => File(relative, Now, runs);

    public FakeDisk File(string relative, DateTime written, params (long Lcn, long Clusters)[] runs)
    {
        string path = Path.Join(Root, relative);
        long clusters = runs.Sum(r => r.Clusters);
        var map = new long[clusters];
        long vcn = 0;
        foreach ((long lcn, long count) in runs)
        {
            for (long i = 0; i < count; i++)
            {
                Assert.False(_used[lcn + i], $"cluster {lcn + i} is used twice");
                _used[lcn + i] = true;
                map[vcn++] = lcn + i;
            }
        }

        _files[path] = map;
        _entries[path] = (false, false, clusters * Volume.BytesPerCluster, written);
        Name(relative);
        Volume = Volume with { FreeBytes = _used.Count(u => !u) * Volume.BytesPerCluster };
        return this;
    }

    public FakeDisk Compress(string relative)
    {
        Compressed.Add(Path.Join(Root, relative));
        return this;
    }

    public FakeDisk Metadata(long lcn, long clusters)
    {
        MetadataRuns.Add((lcn, clusters));
        return Reserve(lcn, clusters);
    }

    public FakeDisk Reserve(long lcn, long clusters)
    {
        for (long i = lcn; i < lcn + clusters; i++)
        {
            _used[i] = true;
        }

        Volume = Volume with { FreeBytes = _used.Count(u => !u) * Volume.BytesPerCluster };
        return this;
    }

    public IReadOnlyList<Extent> ExtentsOf(string relative) => Extents(_files[Path.Join(Root, relative)]);

    public bool IsUsed(long lcn) => _used[lcn];

    public IReadOnlyList<DriveVolume> FixedVolumes() => [Volume];

    public int TryOpen(DriveVolume volume, out IVolumeSession? session)
    {
        Opened++;
        OpenedInBackground |= InBackground;
        session = new Session(this);
        return 0;
    }

    public void EnterBackground() => InBackground = true;

    public void LeaveBackground() => InBackground = false;

    public int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken)
    {
        RetrimCalls++;
        percent(50);
        percent(100);
        return RetrimExit;
    }

    private void Name(string relative)
    {
        string path = Path.Join(Root, relative);
        if (!_ids.ContainsKey(path))
        {
            long id = (FirstRecord + _ids.Count) | Sequence;
            _ids[path] = id;
            _paths[id] = path;
        }
    }

    private bool UnderLink(string path)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null && parent.Length > Root.Length - 1; parent = Path.GetDirectoryName(parent))
        {
            if (_entries.TryGetValue(parent, out var entry) && entry.Link)
            {
                return true;
            }
        }

        return false;
    }

    private long ParentId(string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        return string.Equals(parent, Root, StringComparison.OrdinalIgnoreCase) ? RootId : _ids[parent];
    }

    private static List<Extent> Extents(long[] map)
    {
        var extents = new List<Extent>();
        for (long vcn = 0; vcn < map.Length; vcn++)
        {
            if (extents.Count > 0 && extents[^1].End == map[vcn] && map[vcn] != Unallocated)
            {
                extents[^1] = extents[^1] with { Clusters = extents[^1].Clusters + 1 };
            }
            else
            {
                extents.Add(new Extent(vcn, map[vcn], 1));
            }
        }

        return extents;
    }

    private int Move(string path, long vcn, long lcn, uint clusters)
    {
        Moves.Add(new MoveStep(vcn, lcn, clusters));
        OnMove?.Invoke(Moves.Count);
        if (clusters > MoveChunks.MaxClusters)
        {
            return VolumeNative.ErrorInvalidParameter;
        }

        if (Pinned.Contains(path))
        {
            return VolumeNative.ErrorAccessDenied;
        }

        long[] map = _files[path];
        for (long i = 0; i < clusters; i++)
        {
            if (_used[lcn + i] && map[vcn + i] != lcn + i)
            {
                return VolumeNative.ErrorAccessDenied;
            }
        }

        for (long i = 0; i < clusters; i++)
        {
            _used[map[vcn + i]] = false;
        }

        for (long i = 0; i < clusters; i++)
        {
            map[vcn + i] = lcn + i;
            _used[lcn + i] = true;
        }

        return 0;
    }

    private sealed class Session(FakeDisk disk) : IVolumeSession
    {
        public ClusterBitmap ReadBitmap()
        {
            var bitmap = new ClusterBitmap(disk._used.Length);
            for (long i = 0; i < disk._used.Length; i++)
            {
                if (disk._used[i])
                {
                    bitmap.Set(i, 1, allocated: true);
                }
            }

            return bitmap;
        }

        public int List(string directory, List<VolumeEntry> entries)
        {
            string prefix = directory.EndsWith('\\') ? directory : directory + "\\";
            foreach ((string path, (bool folder, bool link, long size, DateTime written)) in disk._entries)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !path[prefix.Length..].Contains('\\'))
                {
                    entries.Add(new VolumeEntry(path[prefix.Length..], folder, link, size, written, disk.Compressed.Contains(path)));
                }
            }

            return 0;
        }

        public int ReadExtents(string path, List<Extent> extents)
        {
            disk.Reads++;
            disk.OnRead?.Invoke(disk.Reads);
            if (disk._files.TryGetValue(path, out long[]? map))
            {
                extents.AddRange(Extents(map));
            }

            return 0;
        }

        public int ReadFileTable(List<MftRecord> records, Action<int>? progress, CancellationToken cancellationToken)
        {
            disk.FileTableReads++;
            if (!disk.FileTable)
            {
                return VolumeNative.ErrorNotSupported;
            }

            if (disk.FileTableError != 0)
            {
                return disk.FileTableError;
            }

            IEnumerable<KeyValuePair<string, long>> ids = disk.RecordsOutOfOrder ? disk._ids.OrderByDescending(p => p.Value) : disk._ids.OrderBy(p => p.Value);
            foreach ((string path, long id) in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!disk._entries.TryGetValue(path, out var entry) || disk.UnderLink(path))
                {
                    continue;
                }

                FileAttributes attributes = (entry.Directory ? FileAttributes.Directory : FileAttributes.Archive) | (entry.Link ? FileAttributes.ReparsePoint : 0) | (disk.Compressed.Contains(path) ? FileAttributes.Compressed : 0);
                records.Add(new MftRecord(id, disk.ParentId(path), attributes, Path.GetFileName(path)));
                progress?.Invoke(records.Count);
            }

            return 0;
        }

        public int ReadFileRecords(Action<FileRecord> record, Action<int>? progress, CancellationToken cancellationToken, out int damaged)
        {
            damaged = 0;
            disk.DirectReads++;
            if (!disk.DirectRecords)
            {
                return VolumeNative.ErrorNotSupported;
            }

            int emitted = 0;
            foreach ((long lcn, long clusters) in disk.MetadataRuns)
            {
                record(new FileRecord(new MftRecord(Sequence + emitted, RootId, FileAttributes.Hidden | FileAttributes.System, "$Meta"), clusters * disk.Volume.BytesPerCluster, Now, [new Extent(0, lcn, clusters)]));
                progress?.Invoke(++emitted);
            }

            foreach ((string path, long id) in disk._ids.OrderBy(p => p.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!disk._entries.TryGetValue(path, out var entry) || disk.UnderLink(path))
                {
                    continue;
                }

                if (disk.DirectRecordsError != 0 && emitted > 0)
                {
                    return disk.DirectRecordsError;
                }

                FileAttributes attributes = (entry.Directory ? FileAttributes.Directory : FileAttributes.Archive) | (entry.Link ? FileAttributes.ReparsePoint : 0) | (disk.Compressed.Contains(path) ? FileAttributes.Compressed : 0);
                Extent[]? extents = disk.WithoutExtents.Contains(path) ? null
                    : disk._files.TryGetValue(path, out long[]? map) ? [.. Extents(map)]
                    : [];
                record(new FileRecord(new MftRecord(id, disk.ParentId(path), attributes, Path.GetFileName(path)), entry.Size, entry.Written, extents));
                progress?.Invoke(++emitted);
            }

            return disk.DirectRecordsError;
        }

        public int ReadRecord(long id, List<Extent> extents, out RecordFacts facts)
        {
            facts = default;
            disk.Reads++;
            disk.RecordReads.Add(id & MftRecord.RecordMask);
            disk.OnRead?.Invoke(disk.Reads);
            if (!disk._paths.TryGetValue(id, out string? path) || !disk._entries.TryGetValue(path, out var entry))
            {
                return VolumeNative.ErrorFileNotFound;
            }

            facts = new RecordFacts(entry.Size, entry.Written);
            if (disk._files.TryGetValue(path, out long[]? map))
            {
                extents.AddRange(Extents(map));
            }

            return 0;
        }

        public int BeginMove(string path, out IFileMover? mover)
        {
            mover = null;
            if (disk.Locked.Contains(path))
            {
                return VolumeNative.ErrorSharingViolation;
            }

            mover = new Mover(disk, path);
            return 0;
        }

        public int ReadAllocated(long start, long clusters, List<FreeRun> allocated)
        {
            disk.RangeReads++;
            long from = start / 8 * 8;
            long to = Math.Min(disk._used.Length, start + clusters);
            byte[] bits = new byte[(to - from + 7) / 8];
            for (long lcn = from; lcn < to; lcn++)
            {
                if (disk._used[lcn])
                {
                    bits[(lcn - from) / 8] |= (byte)(1 << (int)((lcn - from) % 8));
                }
            }

            AllocatedRuns.Collect(bits, from, start, clusters, allocated);
            return 0;
        }

        public long FileRecords() => disk.Records;

        public int ReadReserved(List<FreeRun> reserved)
        {
            if (disk.ReservedZone.Count == 0)
            {
                return VolumeNative.ErrorNotSupported;
            }

            reserved.AddRange(disk.ReservedZone);
            return 0;
        }

        public IoSample? SampleIo() => disk.BusyQueue is { } queue
            ? new IoSample(0, 0, queue, TimeSpan.FromSeconds(++disk._samples))
            : null;

        public int TrimFreeSpace(long bytes, long chunk, Action<long> trimmed, CancellationToken cancellationToken)
        {
            if (disk.NativeTrimError != 0)
            {
                return disk.NativeTrimError;
            }

            for (long done = Math.Min(chunk, bytes); ; done = Math.Min(bytes, done + chunk))
            {
                trimmed(done);
                if (done >= bytes)
                {
                    break;
                }
            }

            disk.NativeTrimmed = bytes;
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class Mover(FakeDisk disk, string path) : IFileMover
    {
        public int Move(long vcn, long lcn, uint clusters) => disk.Move(path, vcn, lcn, clusters);

        public void Dispose()
        {
        }
    }
}
