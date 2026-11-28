using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class ModePlacementTests
{
    private static DriveAnalysis Analyze(FakeDisk disk) => new DriveAnalyzer(disk).Analyze(disk.Volume, null, CancellationToken.None);

    private static long LastUsed(FakeDisk disk, params string[] files) => files.Max(f => disk.ExtentsOf(f).Max(e => e.End));

    [Fact]
    public void Consolidate_joins_a_file_into_the_lowest_run_that_holds_it_and_never_pushes_data_up()
    {
        FakeDisk disk = new FakeDisk(10_000)
            .File("a.dat", (0, 10))
            .File("b.dat", (30, 10))
            .File("frag.dat", (40, 5), (50, 5), (60, 5))
            .File("x.dat", (100, 100))
            .File("y.dat", (215, 10));
        string[] files = ["a.dat", "b.dat", "frag.dat", "x.dat", "y.dat"];
        long before = LastUsed(disk, files);
        DriveAnalysis analysis = Analyze(disk);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Consolidate, null, null, CancellationToken.None);
        Assert.Equal([new Extent(0, 30, 15)], disk.ExtentsOf("frag.dat"));
        Assert.True(LastUsed(disk, files) < before, $"last used cluster {LastUsed(disk, files)} >= {before}");
        Assert.Equal(1, result.Defragmented);
        Assert.InRange(result.Moved, 1, files.Length);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Consolidate_also_compacts_a_file_it_has_just_joined()
    {
        FakeDisk disk = new FakeDisk(10_000)
            .File("big.dat", (0, 40))
            .File("frag.dat", (100, 5), (200, 5))
            .File("tail.dat", (300, 10));
        DriveAnalysis analysis = Analyze(disk);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Consolidate, null, null, CancellationToken.None);
        Assert.Equal(40, disk.ExtentsOf("tail.dat")[0].Lcn);
        Assert.Equal(50, disk.ExtentsOf("frag.dat")[0].Lcn);
        Assert.Equal(10_000 - 60, analysis.Free.Largest.Length);
        Assert.Equal(2, result.Moved);
    }

    [Fact]
    public void Consolidate_closes_the_gaps_first_so_a_large_file_joins_below_the_data_and_the_end_stays_free()
    {
        FakeDisk disk = new FakeDisk(100_000)
            .File("a.dat", (0, 1_000))
            .File("s1.dat", (8_000, 1_000))
            .File("s2.dat", (9_000, 1_000))
            .File("s3.dat", (10_000, 1_000))
            .File("backup.zip", (11_000, 2_000), (14_000, 2_000), (17_000, 2_000))
            .File("d.dat", (19_000, 1_000));
        string[] files = ["a.dat", "s1.dat", "s2.dat", "s3.dat", "backup.zip", "d.dat"];
        long dataEnd = LastUsed(disk, files);
        DriveAnalysis analysis = Analyze(disk);
        long largest = analysis.Free.Largest.Length;
        Assert.Equal(dataEnd, DefragPlanner.DataEnd(analysis));
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Consolidate, null, null, CancellationToken.None);
        Extent joined = Assert.Single(disk.ExtentsOf("backup.zip"));
        Assert.True(joined.End <= dataEnd, $"the archive ends at {joined.End}, past the data end {dataEnd}");
        Assert.Equal(11_000, LastUsed(disk, files));
        Assert.Equal(new FreeRun(11_000, 89_000), analysis.Free.Largest);
        Assert.True(analysis.Free.Largest.Length > largest);
        Assert.Equal((1, 0, 0), (result.Defragmented, result.NoRoom, result.Failed));
        Assert.Equal(5, result.Moved);
    }

    [Fact]
    public void Consolidate_leaves_a_large_file_in_pieces_rather_than_push_it_past_the_data()
    {
        FakeDisk disk = new FakeDisk(100_000)
            .File("a.dat", (0, 1_000))
            .File("b.dat", (3_000, 1_000))
            .File("c.dat", (6_000, 1_000))
            .File("backup.zip", (7_000, 3_000), (12_000, 3_000), (20_000, 3_000))
            .File("d.dat", (23_000, 1_000));
        string[] files = ["a.dat", "b.dat", "c.dat", "backup.zip", "d.dat"];
        long dataEnd = LastUsed(disk, files);
        DriveAnalysis analysis = Analyze(disk);
        long largest = analysis.Free.Largest.Length;
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.Consolidate, null, null, CancellationToken.None);
        Assert.Equal(3, disk.ExtentsOf("backup.zip").Count);
        Assert.True(LastUsed(disk, files) <= dataEnd);
        Assert.False(Enumerable.Range((int)dataEnd, 100_000 - (int)dataEnd).Any(lcn => disk.IsUsed(lcn)), "a cluster past the data end was used");
        Assert.True(analysis.Free.Largest.Length >= largest);
        Assert.Equal(100_000, analysis.Free.Largest.End);
        Assert.Equal((0, 1, 0), (result.Defragmented, result.NoRoom, result.Failed));
    }

    [Fact]
    public void Full_zone_moves_other_files_out_of_the_fast_zone_before_it_places_programs()
    {
        FakeDisk disk = new FakeDisk(10_000)
            .File("one.txt", (0, 300))
            .File("two.txt", (300, 300))
            .File("app.exe", (2_000, 200))
            .File("lib.dll", (3_000, 200));
        DriveAnalysis analysis = Analyze(disk);
        ZoneLayout layout = analysis.Layout;
        Assert.True(layout.FastEnd <= 600);
        DefragResult result = new DriveDefragmenter(disk).Run(analysis, DefragMode.FullZone, null, null, CancellationToken.None);
        Assert.Equal((0, 0), (result.NoRoom, result.Failed));
        foreach (string program in (string[])["app.exe", "lib.dll"])
        {
            Extent placed = Assert.Single(disk.ExtentsOf(program));
            Assert.True(layout.Holds(SpeedZone.Fast, placed.Lcn, placed.Clusters), $"{program} at {placed.Lcn}");
        }

        foreach (string document in (string[])["one.txt", "two.txt"])
        {
            Extent placed = Assert.Single(disk.ExtentsOf(document));
            Assert.True(layout.Holds(SpeedZone.Standard, placed.Lcn, placed.Clusters), $"{document} at {placed.Lcn}");
        }
    }

    [Fact]
    public void Quick_defrag_still_uses_the_best_fit_to_keep_large_runs_whole()
    {
        FakeDisk disk = new FakeDisk(10_000)
            .File("a.dat", (0, 10))
            .File("b.dat", (30, 10))
            .File("frag.dat", (40, 5), (50, 5), (60, 5))
            .File("x.dat", (100, 100))
            .File("y.dat", (215, 10));
        DriveAnalysis analysis = Analyze(disk);
        new DriveDefragmenter(disk).Run(analysis, DefragMode.Quick, null, null, CancellationToken.None);
        Assert.Equal(200, disk.ExtentsOf("frag.dat")[0].Lcn);
    }
}
