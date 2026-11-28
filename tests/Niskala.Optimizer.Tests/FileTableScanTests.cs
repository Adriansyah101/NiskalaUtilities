using System.Buffers.Binary;
using System.Text;
using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class UsnRecordTests
{
    private const long Root = 0x0005_0000_0000_0005;

    [Fact]
    public void Version_2_and_3_records_give_their_ids_parents_attributes_and_names()
    {
        byte[] buffer = [.. V2(0x0001_0000_0000_0040, Root, FileAttributes.Directory, "Games"), .. V3(0x0001_0000_0000_0041, 0x0001_0000_0000_0040, FileAttributes.Archive, "setup.exe")];
        var records = new List<MftRecord>();
        Assert.True(VolumeNative.ParseUsnRecords(buffer, records));
        Assert.Equal(2, records.Count);
        Assert.Equal(new MftRecord(0x0001_0000_0000_0040, Root, FileAttributes.Directory, "Games"), records[0]);
        Assert.Equal(new MftRecord(0x0001_0000_0000_0041, 0x0001_0000_0000_0040, FileAttributes.Archive, "setup.exe"), records[1]);
        Assert.Equal(0x41, records[1].Number);
        Assert.True(records[0].IsDirectory);
        Assert.False(records[1].IsDirectory);
    }

    [Fact]
    public void Ids_wider_than_64_bits_and_broken_records_are_refused()
    {
        byte[] wide = V3(0x41, Root, FileAttributes.Archive, "a.txt");
        BinaryPrimitives.WriteInt64LittleEndian(wide.AsSpan(16), 1);
        Assert.False(VolumeNative.ParseUsnRecords(wide, []));
        byte[] shortLength = V2(0x41, Root, FileAttributes.Archive, "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(shortLength, 4);
        Assert.False(VolumeNative.ParseUsnRecords(shortLength, []));
        byte[] longName = V2(0x41, Root, FileAttributes.Archive, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(longName.AsSpan(56), 200);
        Assert.False(VolumeNative.ParseUsnRecords(longName, []));
        byte[] unknown = V2(0x41, Root, FileAttributes.Archive, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4), 4);
        Assert.False(VolumeNative.ParseUsnRecords(unknown, []));
        Assert.True(VolumeNative.ParseUsnRecords([], []));
    }

    private static byte[] V2(long id, long parent, FileAttributes attributes, string name) => Record(2, 60, id, parent, attributes, name);

    private static byte[] V3(long id, long parent, FileAttributes attributes, string name) => Record(3, 76, id, parent, attributes, name);

    private static byte[] Record(ushort major, int header, long id, long parent, FileAttributes attributes, string name)
    {
        byte[] text = Encoding.Unicode.GetBytes(name);
        int length = (header + text.Length + 7) / 8 * 8;
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), major);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), id);
        int parentAt = major == 2 ? 16 : 24;
        int attributesAt = major == 2 ? 52 : 68;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(parentAt), parent);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attributesAt), (uint)attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(attributesAt + 4), (ushort)text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(attributesAt + 6), (ushort)header);
        text.CopyTo(record.AsSpan(header));
        return record;
    }
}

public sealed class FileTablePathTests
{
    private const long Root = 0x0005_0000_0000_0005;

    [Fact]
    public void Folders_are_rebuilt_from_parent_references_in_any_order()
    {
        var paths = FileTablePaths.From(@"E:\", [
            new MftRecord(0x30, 0x20, FileAttributes.Directory, "Saves"),
            new MftRecord(0x20, 0x10, FileAttributes.Directory, "Game"),
            new MftRecord(0x10, Root, FileAttributes.Directory, "Games"),
            new MftRecord(0x40, 0x30, FileAttributes.Archive, "slot1.dat"),
        ]);
        Assert.Equal(3, paths.Folders);
        TableFolder saves = paths.Resolve(0x30)!.Value;
        Assert.Equal(@"E:\Games\Game\Saves", saves.Full);
        Assert.Equal(@"Games\Game\Saves", saves.Relative);
        Assert.False(saves.IsProtected);
        Assert.Same(saves.Full, paths.Resolve(0x30)!.Value.Full);
        Assert.Equal(@"E:\", paths.Resolve(Root)!.Value.Full);
        Assert.Equal(@"E:\Games", paths.Resolve(0x10)!.Value.Full);
    }

    [Fact]
    public void Orphans_and_loops_have_no_path()
    {
        var paths = FileTablePaths.From(@"E:\", [
            new MftRecord(0x10, 0x99, FileAttributes.Directory, "Lost"),
            new MftRecord(0x20, 0x21, FileAttributes.Directory, "A"),
            new MftRecord(0x21, 0x20, FileAttributes.Directory, "B"),
            new MftRecord(0x22, Root, FileAttributes.Directory | FileAttributes.ReparsePoint, "Junction"),
        ]);
        Assert.Null(paths.Resolve(0x10));
        Assert.Null(paths.Resolve(0x20));
        Assert.Null(paths.Resolve(0x21));
        Assert.Null(paths.Resolve(0x22));
        Assert.Null(paths.Resolve(0x99));
    }

    [Fact]
    public void A_system_folder_protects_everything_below_it_at_any_depth()
    {
        var paths = FileTablePaths.From(@"E:\", [
            new MftRecord(0x10, Root, FileAttributes.Directory, "Backup"),
            new MftRecord(0x11, 0x10, FileAttributes.Directory, ProtectedFiles.VolumeInformation),
            new MftRecord(0x12, 0x11, FileAttributes.Directory, "Snapshots"),
        ]);
        Assert.False(paths.Resolve(0x10)!.Value.IsProtected);
        Assert.True(paths.Resolve(0x11)!.Value.IsProtected);
        Assert.True(paths.Resolve(0x12)!.Value.IsProtected);
    }

    [Fact]
    public void Only_ntfs_uses_the_file_table()
    {
        Assert.True(FileTableScan.Supports(new FakeDisk(10).Volume));
        Assert.False(FileTableScan.Supports(new FakeDisk(10, fileSystem: "FAT32").Volume));
        Assert.False(FileTableScan.Supports(new FakeDisk(10, fileSystem: "exFAT").Volume));
    }
}

public sealed class FileTableAnalysisTests
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
        .Folder(@"Apps\Deep")
        .Folder(@"Apps\Deep\Deeper")
        .File(@"Apps\Deep\Deeper\data.bin", (600, 2), (610, 2))
        .Link("Junction")
        .File(@"Junction\loop.txt", (500, 2));

    [Fact]
    public void Ntfs_is_read_from_the_file_table_with_the_same_result_as_the_folder_walk()
    {
        FakeDisk walked = SampleDisk();
        DriveAnalysis byFolders = new DriveAnalyzer(walked).Analyze(walked.Volume, null, CancellationToken.None);
        FakeDisk indexed = SampleDisk();
        indexed.FileTable = true;
        DriveAnalysis byTable = new DriveAnalyzer(indexed).Analyze(indexed.Volume, null, CancellationToken.None);
        Assert.Equal(ScanMethod.Folders, byFolders.Method);
        Assert.Equal(ScanMethod.FileTable, byTable.Method);
        Assert.Equal(byFolders.Files.Select(f => f.Path).Order(), byTable.Files.Select(f => f.Path).Order());
        Assert.Equal(byFolders.Map.Totals(), byTable.Map.Totals());
        DriveSummary summary = byTable.Summary();
        Assert.Equal((2, 2), (summary.FragmentedFiles, summary.ExcessFragments));
        Assert.Equal(SpeedZone.Fast, byTable.Files.Single(f => f.Name == "tool.exe").Zone);
        Assert.Equal(1, byTable.Counts.Links);
        Assert.Equal(3, summary.Protected);
        Assert.Equal(1, indexed.FileTableReads);
    }

    [Fact]
    public void Records_are_opened_in_file_table_order_even_when_listed_out_of_order()
    {
        FakeDisk disk = SampleDisk();
        disk.FileTable = true;
        disk.RecordsOutOfOrder = true;
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Equal(ScanMethod.FileTable, analysis.Method);
        Assert.NotEmpty(disk.RecordReads);
        Assert.Equal(disk.RecordReads.Order(), disk.RecordReads);
        Assert.Equal(@"X:\Apps\Deep\Deeper\data.bin", analysis.Files.Single(f => f.Name == "data.bin").Path);
        Assert.Same(analysis.Files.Single(f => f.Name == "tool.exe").Folder, analysis.Files.Single(f => f.Name == "notes.txt").Folder);
    }

    [Theory]
    [InlineData("FAT32", 0)]
    [InlineData("exFAT", 0)]
    [InlineData("NTFS", VolumeNative.ErrorAccessDenied)]
    [InlineData("NTFS", VolumeNative.ErrorNotSupported)]
    public void Other_file_systems_and_a_refused_file_table_walk_the_folders(string fileSystem, int error)
    {
        FakeDisk disk = new FakeDisk(1_000, fileSystem: fileSystem).Folder("Data").File(@"Data\a.dat", (10, 2), (20, 2)).File("b.dat", (30, 2));
        disk.FileTable = true;
        disk.FileTableError = error;
        DriveAnalysis analysis = new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);
        Assert.Equal(ScanMethod.Folders, analysis.Method);
        Assert.Equal([@"X:\b.dat", @"X:\Data\a.dat"], analysis.Files.Select(f => f.Path).Order());
        Assert.Empty(disk.RecordReads);
        Assert.Equal(error == 0 ? 0 : 1, disk.FileTableReads);
    }

    [Fact]
    public void Reading_the_file_table_has_its_own_phase_and_progress_never_goes_back()
    {
        FakeDisk disk = new FakeDisk(100_000) { Records = 4_000 };
        disk.FileTable = true;
        disk.Folder("Bulk");
        for (int i = 0; i < 3_000; i++)
        {
            disk.File($@"Bulk\{i:x8}.dat", (i * 3, 2));
        }

        var reports = new List<DriveProgress>();
        new DriveAnalyzer(disk).Analyze(disk.Volume, new Inline(reports.Add), CancellationToken.None);
        Assert.Contains(reports, r => r.Phase == DrivePhase.ReadingFileTable);
        Assert.True(reports.FindIndex(r => r.Phase == DrivePhase.ReadingFileTable) < reports.FindIndex(r => r.Phase == DrivePhase.Scanning));
        double last = 0;
        foreach (DriveProgress report in reports)
        {
            Assert.True(report.Percent >= last, $"{report.Phase} {report.Percent} < {last}");
            last = report.Percent;
        }

        Assert.Equal(DrivePhase.Finished, reports[^1].Phase);
    }

    [Fact]
    public void Cancelling_stops_the_file_table_scan_at_once()
    {
        FakeDisk disk = new FakeDisk(10_000);
        disk.FileTable = true;
        for (int i = 0; i < 2_000; i++)
        {
            disk.File($"{i:x8}", (i * 2, 1));
        }

        using var cancel = new CancellationTokenSource();
        disk.OnRead = reads =>
        {
            if (reads == 10)
            {
                cancel.Cancel();
            }
        };
        Assert.Throws<OperationCanceledException>(() => new DriveAnalyzer(disk).Analyze(disk.Volume, null, cancel.Token));
        Assert.InRange(disk.Reads, 10, 11);
    }

    [Theory]
    [InlineData(0, 1_000, 0)]
    [InlineData(500, 1_000, 12.5)]
    [InlineData(2_000, 1_000, 25)]
    [InlineData(10, 0, 0)]
    public void The_file_table_takes_the_first_quarter_of_the_bar(long read, long expected, double percent) =>
        Assert.Equal(percent, ScanProgress.Table(read, expected), 3);

    [Fact]
    public void The_scan_after_the_file_table_fills_the_rest_of_the_bar()
    {
        Assert.Equal(ScanProgress.TableShare, ScanProgress.AfterTable(0), 3);
        Assert.Equal(25 + 37.5, ScanProgress.AfterTable(50), 3);
        Assert.Equal(ScanProgress.Ceiling, ScanProgress.AfterTable(100), 3);
    }

    [Fact]
    public void The_analysis_throttle_only_backs_off_for_other_programs_queued_on_the_disk()
    {
        var throttle = new IoThrottleController(queueOnly: true);
        throttle.Next(new IoSample(0, 0, 0, TimeSpan.Zero), 0);
        throttle.Next(new IoSample(200L << 20, 0, 1, TimeSpan.FromSeconds(1)), 0);
        Assert.False(throttle.IsUserBusy);
        throttle.Next(new IoSample(200L << 20, 0, IoThrottleController.BusyQueueDepth, TimeSpan.FromSeconds(2)), 0);
        Assert.True(throttle.IsUserBusy);
        Assert.True(DriveAnalyzer.NewThrottle().QueueOnly);
        Assert.False(new IoThrottleController().QueueOnly);
    }

    private sealed class Inline(Action<DriveProgress> report) : IProgress<DriveProgress>
    {
        public void Report(DriveProgress value) => report(value);
    }
}
