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
        Assert.Equal([new Extent(0, 10, 15)], disk.ExtentsOf("frag.dat"));
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
        Assert.Equal(40, disk.ExtentsOf("frag.dat")[0].Lcn);
        Assert.Equal(50, disk.ExtentsOf("tail.dat")[0].Lcn);
        Assert.Equal(10_000 - 60, analysis.Free.Largest.Length);
        Assert.Equal(2, result.Moved);
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
