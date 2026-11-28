using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class MftRecordReaderTests
{
    private const ushort RootSequence = 5;
    private static readonly long RootId = RecordBuilder.Id(5, RootSequence);
    private static readonly long GamesId = RecordBuilder.Id(16, 3);
    private static readonly DateTime Written = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Resident_non_resident_sparse_and_extension_records_are_read_without_opening_files()
    {
        MftImage image = Sample(cluster: 4096, (16, 4), (40, 4));
        List<FileRecord> records = image.ReadAll(out int error, out int damaged);
        Assert.Equal(0, error);
        Assert.Equal(1, damaged);
        Assert.Equal([5, 11, 16, 17, 18, 19, 26, 27, 0, 20, 24], records.Select(r => r.Entry.Number));
        FileRecord root = records.Single(r => r.Entry.Number == 5);
        Assert.True(root.Entry.IsDirectory);
        Assert.Equal([new Extent(0, 300, 1)], root.Extents!);
        FileRecord extend = records.Single(r => r.Entry.Number == 11);
        Assert.Empty(extend.Extents!);
        FileRecord tiny = records.Single(r => r.Entry.Name == "tiny.txt");
        Assert.True(tiny.IsResident);
        Assert.Equal(100, tiny.Size);
        Assert.Equal(Written, tiny.LastWriteUtc);
        Assert.Equal(new MftRecord(RecordBuilder.Id(17, 9), GamesId, FileAttributes.Archive, "tiny.txt"), tiny.Entry);
        FileRecord big = records.Single(r => r.Entry.Number == 18);
        Assert.Equal("big.bin", big.Entry.Name);
        Assert.Equal([new Extent(0, 500, 10), new Extent(10, 600, 5)], big.Extents!);
        Assert.Equal((15 * 4096) - 100, big.Size);
        FileRecord sparse = records.Single(r => r.Entry.Name == "sparse.vhd");
        Assert.Equal([new Extent(0, 700, 2), new Extent(2, -1, 3), new Extent(5, 710, 1)], sparse.Extents!);
        FileRecord huge = records.Single(r => r.Entry.Name == "huge.iso");
        Assert.Equal([new Extent(0, 800, 10), new Extent(10, 900, 20)], huge.Extents!);
        Assert.Equal(30 * 4096, huge.Size);
        Assert.True(records.Single(r => r.Entry.Name == "orphan.dat").NeedsHandle);
        FileRecord streams = records.Single(r => r.Entry.Name == "doc.txt");
        Assert.True(streams.IsResident);
        Assert.Equal(10, streams.Size);
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReparsePoint, records.Single(r => r.Entry.Name == "link").Entry.Attributes);
    }

    [Fact]
    public void Records_that_straddle_two_pieces_of_the_file_table_are_joined_when_clusters_are_smaller_than_records()
    {
        MftImage image = Sample(cluster: 512, (100, 3), (400, 61));
        List<FileRecord> records = image.ReadAll(out int error, out int damaged);
        Assert.Equal(0, error);
        Assert.Equal(1, damaged);
        Assert.Equal(11, records.Count);
        Assert.Contains(records, r => r.Entry.Name == "$MFT" && r.Extents!.Length == 2);
    }

    [Fact]
    public void A_torn_record_is_counted_as_damaged_and_skipped()
    {
        var bytes = new RecordBuilder(1).Standard(Written).Name(RootId, "a.txt").ResidentData(4).Build(1024);
        Span<byte> torn = bytes.AsSpan().ToArray();
        torn[510] ^= 0x55;
        Assert.False(NtfsMftRecordReader.TryFixup(torn));
        Span<byte> whole = bytes.AsSpan().ToArray();
        Assert.True(NtfsMftRecordReader.TryFixup(whole));
    }

    [Fact]
    public void Mapping_pairs_decode_signed_offsets_sparse_runs_and_reject_broken_input()
    {
        var runs = new List<Extent>();
        Assert.True(NtfsMftRecordReader.TryDecodeRuns(RecordBuilder.Pairs((5000, 8), (-1, 4), (100, 2), (70_000, 300)), 0, runs));
        Assert.Equal([new Extent(0, 5000, 8), new Extent(8, -1, 4), new Extent(12, 100, 2), new Extent(14, 70_000, 300)], runs);
        runs.Clear();
        Assert.False(NtfsMftRecordReader.TryDecodeRuns([0x11, 0x08], 0, runs));
        Assert.False(NtfsMftRecordReader.TryDecodeRuns([0x11, 0x08, 0x10], 0, runs));
        runs.Clear();
        Assert.False(NtfsMftRecordReader.TryDecodeRuns([0x11, 0x04, 0x80, 0x00], 0, runs));
        Assert.False(NtfsMftRecordReader.TryDecodeRuns([0x01, 0x00, 0x00], 0, runs));
    }

    [Fact]
    public void Pieces_with_a_gap_or_short_of_the_allocated_size_are_left_to_the_slow_path()
    {
        StreamPiece first = new(StreamKind.Data, 0, 9, 30 * 4096, 30 * 4096, [new Extent(0, 800, 10)]);
        StreamPiece rest = new(StreamKind.Data, 10, 29, 0, 0, [new Extent(10, 900, 20)]);
        StreamPiece late = new(StreamKind.Data, 12, 29, 0, 0, [new Extent(12, 900, 18)]);
        Assert.NotNull(NtfsMftRecordReader.Join([rest, first], StreamKind.Data, 4096, out long size));
        Assert.Equal(30 * 4096, size);
        Assert.Null(NtfsMftRecordReader.Join([first, late], StreamKind.Data, 4096, out _));
        Assert.Null(NtfsMftRecordReader.Join([first], StreamKind.Data, 4096, out _));
        Assert.Null(NtfsMftRecordReader.Join([first with { Runs = null }, rest], StreamKind.Data, 4096, out _));
        Assert.Null(NtfsMftRecordReader.Join([rest], StreamKind.Data, 4096, out _));
    }

    [Fact]
    public void A_file_table_whose_own_record_cannot_be_trusted_is_refused_so_the_next_method_runs()
    {
        MftImage image = new(2_000, 4096, 1024, 16, (16, 4));
        image.Put(0, new RecordBuilder(1).Standard(Written).Name(RootId, "$MFT").Data(0, 1, 4 * 4096, 16 * 1024, (16, 2)).Build(1024));
        image.ReadAll(out int partial, out _);
        Assert.Equal(VolumeNative.ErrorNotSupported, partial);
        MftImage blank = new(2_000, 4096, 1024, 16, (16, 4));
        blank.ReadAll(out int empty, out _);
        Assert.Equal(VolumeNative.ErrorNotSupported, empty);
        var unusable = new MftGeometry(4096, 512, 1000, 16, 16 * 1000);
        Assert.False(unusable.IsUsable);
        Assert.Equal(VolumeNative.ErrorNotSupported, NtfsMftRecordReader.Read(unusable, image.Read, _ => { }, null, CancellationToken.None, out _));
    }

    [Fact]
    public void A_read_error_part_way_stops_the_reader_and_is_returned()
    {
        MftImage image = Sample(cluster: 4096, (16, 4), (40, 4));
        image.FailAfter = 2;
        image.FailWith = VolumeNative.ErrorAccessDenied;
        image.ReadAll(out int error, out _);
        Assert.Equal(VolumeNative.ErrorAccessDenied, error);
    }

    [Fact]
    public void Cancelling_stops_the_reader_between_chunks()
    {
        MftImage image = Sample(cluster: 4096, (16, 4), (40, 4));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => NtfsMftRecordReader.Read(image.Geometry, image.Read, _ => { }, null, cancel.Token, out _));
    }

    private static MftImage Sample(int cluster, params (long Lcn, long Clusters)[] runs)
    {
        const int Record = 1024;
        const int Slots = 32;
        var image = new MftImage(4_000, cluster, Record, Slots, runs);
        long allocated = runs.Sum(r => r.Clusters) * cluster;
        image.Put(0, new RecordBuilder(1).Standard(Written, FileAttributes.Hidden | FileAttributes.System).AttributeList().Name(RootId, "$MFT")
            .Data(0, runs.Sum(r => r.Clusters) - 1, allocated, Slots * Record, runs).Build(Record));
        image.Put(5, new RecordBuilder(RootSequence, directory: true).Standard(Written, FileAttributes.Hidden | FileAttributes.System).Name(RootId, ".")
            .Index(4096, (300, 1)).Build(Record));
        image.Put(11, new RecordBuilder(11, directory: true).Standard(Written, FileAttributes.Hidden | FileAttributes.System).Name(RootId, "$Extend").Build(Record));
        image.Put(16, new RecordBuilder(3, directory: true).Standard(Written).Name(RootId, "Games").Build(Record));
        image.Put(17, new RecordBuilder(9).Standard(Written).Name(GamesId, "tiny.txt").ResidentData(100).Build(Record));
        image.Put(18, new RecordBuilder(2).Standard(Written).Name(GamesId, "BIG~1.BIN", RecordBuilder.Dos).Name(GamesId, "big.bin")
            .Data(0, 14, 15 * 4096, (15 * 4096) - 100, (500, 10), (600, 5)).Build(Record));
        image.Put(19, new RecordBuilder(2).Standard(Written).Name(GamesId, "sparse.vhd")
            .Data(0, 5, 6 * 4096, 6 * 4096, (700, 2), (-1, 3), (710, 1)).Build(Record));
        image.Put(20, new RecordBuilder(4).Standard(Written).AttributeList().Name(GamesId, "huge.iso")
            .Data(0, 9, 30 * 4096, 30 * 4096, (800, 10)).Build(Record));
        image.Put(21, new RecordBuilder(4, baseRecord: RecordBuilder.Id(20, 4))
            .Data(10, 29, 0, 0, (900, 20)).Build(Record));
        image.Put(22, new RecordBuilder(6, inUse: false).Standard(Written).Name(GamesId, "deleted.tmp").ResidentData(5).Build(Record));
        image.Put(23, new RecordBuilder(2).Standard(Written).Name(GamesId, "torn.dat").ResidentData(5).Build(Record));
        image.Tear(23, 0);
        image.Put(24, new RecordBuilder(2).Standard(Written).AttributeList().Name(GamesId, "orphan.dat")
            .Data(0, 9, 40 * 4096, 40 * 4096, (1000, 10)).Build(Record));
        image.Put(26, new RecordBuilder(2).Standard(Written).Name(GamesId, "doc.txt").ResidentData(10)
            .NonResident(NtfsMftRecordReader.Data, "Zone", 0, 0, 4096, 20, (1200, 1)).Build(Record));
        image.Put(27, new RecordBuilder(2).Standard(Written, FileAttributes.Hidden | FileAttributes.ReparsePoint).Name(GamesId, "link").ResidentData(0).Build(Record));
        return image;
    }
}

public sealed class DirectRecordAnalysisTests
{
    private static FakeDisk SampleDisk() => new FakeDisk(1_000)
        .Reserve(0, 20)
        .File("pagefile.sys", (20, 30))
        .Folder("System Volume Information")
        .File(@"System Volume Information\snap", (50, 5))
        .Folder("Apps")
        .File(@"Apps\tool.exe", (100, 4), (200, 4))
        .File(@"Apps\film.mkv", (300, 40))
        .File(@"Apps\notes.txt", (400, 3))
        .File(@"Apps\empty.ini")
        .Folder(@"Apps\Deep")
        .Folder(@"Apps\Deep\Deeper")
        .File(@"Apps\Deep\Deeper\data.bin", (600, 2), (610, 2))
        .Link("Junction")
        .File(@"Junction\loop.txt", (500, 2));

    [Fact]
    public void File_records_give_the_same_map_as_the_folder_walk_without_opening_a_single_file()
    {
        FakeDisk walked = SampleDisk();
        DriveAnalysis byFolders = new DriveAnalyzer(walked).Analyze(walked.Volume, null, CancellationToken.None);
        FakeDisk direct = SampleDisk();
        direct.DirectRecords = true;
        direct.FileTable = true;
        DriveAnalysis byRecords = new DriveAnalyzer(direct).Analyze(direct.Volume, null, CancellationToken.None);
        Assert.Equal(ScanMethod.FileRecords, byRecords.Method);
        Assert.Equal(byFolders.Files.Select(f => f.Path).Order(), byRecords.Files.Select(f => f.Path).Order());
        Assert.Equal(byFolders.Map.Totals(), byRecords.Map.Totals());
        Assert.Equal(byFolders.Summary() with { Protected = 3 }, byRecords.Summary());
        Assert.Empty(direct.RecordReads);
        Assert.Equal(0, direct.Reads);
        Assert.Equal(0, direct.FileTableReads);
        Assert.Equal(0, byRecords.Counts.Opened);
        Assert.Equal(1, byRecords.Summary().ResidentFiles);
        Assert.Equal(1, byRecords.Counts.Links);
        Assert.Equal(3, byRecords.Summary().Protected);
        Assert.Equal(@"X:\Apps\Deep\Deeper\data.bin", byRecords.Files.Single(f => f.Name == "data.bin").Path);
        Assert.Same(byRecords.Files.Single(f => f.Name == "tool.exe").Folder, byRecords.Files.Single(f => f.Name == "notes.txt").Folder);
    }

    [Fact]
    public void Only_records_whose_clusters_could_not_be_read_from_the_table_are_opened()
    {
        FakeDisk disk = SampleDisk();
        disk.DirectRecords = true;
        disk.WithoutExtents.Add(@"X:\Apps\film.mkv");
        disk.WithoutExtents.Add(@"X:\Apps\Deep");
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Equal(ScanMethod.FileRecords, analysis.Method);
        long[] opened = [disk.IdOf(@"Apps\Deep") & MftRecord.RecordMask, disk.IdOf(@"Apps\film.mkv") & MftRecord.RecordMask];
        Assert.Equal(opened.Order(), disk.RecordReads.Order());
        Assert.Equal(2, analysis.Counts.Opened);
        Assert.Equal([new Extent(0, 300, 40)], analysis.Files.Single(f => f.Name == "film.mkv").Extents);
    }

    [Theory]
    [InlineData(true, ScanMethod.FileTable, 3)]
    [InlineData(false, ScanMethod.Folders, 2)]
    public void A_failed_record_read_falls_back_to_the_next_method_and_drops_what_it_had_read(bool fileTable, ScanMethod expected, int protectedFiles)
    {
        FakeDisk disk = SampleDisk();
        disk.DirectRecords = true;
        disk.DirectRecordsError = VolumeNative.ErrorAccessDenied;
        disk.FileTable = fileTable;
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Equal(expected, analysis.Method);
        Assert.Equal(1, disk.DirectReads);
        Assert.Equal(analysis.Files.Count, analysis.Files.Select(f => f.Path).Distinct().Count());
        Assert.Equal(4, analysis.Files.Count);
        Assert.Equal(protectedFiles, analysis.Summary().Protected);
    }

    [Fact]
    public void Reading_file_records_reports_progress_that_never_goes_back()
    {
        FakeDisk disk = SampleDisk();
        disk.DirectRecords = true;
        disk.Records = 16;
        var seen = new List<DriveProgress>();
        new DriveAnalyzer(disk).Analyze(disk.Volume, new Inline(seen.Add), CancellationToken.None);
        Assert.Contains(seen, p => p.Phase == DrivePhase.ReadingFileTable);
        double[] percents = [.. seen.Select(p => p.Percent)];
        Assert.Equal(percents.Order(), percents);
        Assert.Equal(DrivePhase.Finished, seen[^1].Phase);
    }

    private sealed class Inline(Action<DriveProgress> report) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => report(value);
    }
}
