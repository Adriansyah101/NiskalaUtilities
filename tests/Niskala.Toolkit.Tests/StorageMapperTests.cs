using Niskala.Toolkit.Storage;

namespace Niskala.Toolkit.Tests;

public sealed class StorageMapperTests
{
    [Fact]
    public async Task Sizes_counts_and_extensions_add_up_across_threads()
    {
        using var scratch = new ScratchFolder();
        scratch.File(@"media\a.mp4", new byte[5000]);
        scratch.File(@"media\b.MP4", new byte[3000]);
        scratch.File(@"docs\deep\c.txt", new byte[100]);
        scratch.File(@"docs\README", new byte[10]);
        for (int i = 0; i < 40; i++)
        {
            scratch.File($@"many\f{i}\x.bin", new byte[i]);
        }

        StorageMap map = await StorageMapper.MapAsync(scratch.Root, new StorageMapOptions { Workers = 4 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8000 + 110 + 780, map.Root.Bytes);
        Assert.Equal(44, map.Root.Files);
        Assert.Equal(44, map.Root.Folders);
        Assert.Equal(["media", "many", "docs"], map.Root.Children.Select(c => c.Name));
        Assert.Equal((2, 8000L), (map.Extensions[0].Files, map.Extensions[0].Bytes));
        Assert.Equal(".mp4", map.Extensions[0].Extension);
        Assert.Contains(map.Extensions, e => e.Extension == "(none)" && e.Bytes == 10);
    }

    [Fact]
    public async Task Junctions_are_counted_but_never_walked()
    {
        using var scratch = new ScratchFolder();
        string outside = scratch.File(@"outside\big.bin", new byte[100_000]);
        scratch.File(@"inside\small.bin", new byte[10]);
        scratch.Link("/J", @"inside\loop", scratch.Root);
        scratch.Link("/J", @"inside\out", Path.GetDirectoryName(outside)!);

        StorageMap map = await StorageMapper.MapAsync(scratch.Path("inside"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((10L, 1, 2), (map.Root.Bytes, map.Root.Files, map.Links));
        Assert.Empty(map.Root.Children);
    }

    [Fact]
    public async Task Child_lists_are_trimmed_but_totals_keep_everything()
    {
        using var scratch = new ScratchFolder();
        for (int i = 0; i < 10; i++)
        {
            scratch.File($@"f{i}\x.bin", new byte[100 + i]);
        }

        StorageMap map = await StorageMapper.MapAsync(scratch.Root, new StorageMapOptions { ChildrenPerFolder = 3 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["f9", "f8", "f7"], map.Root.Children.Select(c => c.Name));
        Assert.Equal(1045, map.Root.Bytes);
        Assert.Equal(10, map.Root.Folders);
    }

    [Fact]
    public async Task A_missing_folder_is_an_error() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => StorageMapper.MapAsync(Path.Combine(TestRoots.Folder, "absent"), cancellationToken: TestContext.Current.CancellationToken));
}
