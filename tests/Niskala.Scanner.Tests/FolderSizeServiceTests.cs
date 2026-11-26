using Niskala.Scanner.Sizing;

namespace Niskala.Scanner.Tests;

public sealed class FolderSizeServiceTests : IDisposable
{
    private readonly ScratchFolder _scratch = new();
    private readonly FolderSizeService _service = new(new DiskKindProbe());

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void Counts_files_folders_and_bytes_including_hidden_files()
    {
        _scratch.File(@"app\a.bin", 1000);
        _scratch.File(@"app\sub\b.bin", 2500);
        string hidden = _scratch.File(@"app\sub\deeper\.hidden", 500);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        FolderSize size = _service.Measure(new SizeRequest("x", Path.Combine(_scratch.Root, "app")), TestContext.Current.CancellationToken);

        Assert.True(size.Exists);
        Assert.Equal(4000, size.Bytes);
        Assert.Equal(3, size.Files);
        Assert.Equal(2, size.Directories);
        Assert.Null(size.Error);
    }

    [Fact]
    public void Junctions_inside_are_not_followed()
    {
        _scratch.File(@"target\big.bin", 50_000);
        _scratch.File(@"app\a.bin", 10);
        _scratch.Junction(@"app\link", Path.Combine(_scratch.Root, "target"));

        FolderSize size = _service.Measure(new SizeRequest("x", Path.Combine(_scratch.Root, "app")), TestContext.Current.CancellationToken);

        Assert.Equal(10, size.Bytes);
        Assert.Equal(1, size.Directories);
    }

    [Fact]
    public void A_junction_root_is_reported_as_a_link()
    {
        _scratch.File(@"target\big.bin", 50_000);
        string link = _scratch.Junction("link", Path.Combine(_scratch.Root, "target"));

        FolderSize size = _service.Measure(new SizeRequest("x", link), TestContext.Current.CancellationToken);

        Assert.True(size.IsLink);
        Assert.Equal(0, size.Bytes);
    }

    [Fact]
    public void Missing_folder_is_reported_not_thrown()
    {
        Assert.False(_service.Measure(new SizeRequest("x", Path.Combine(_scratch.Root, "missing")), TestContext.Current.CancellationToken).Exists);
    }

    [Fact]
    public async Task Measures_many_folders_concurrently_and_returns_each_once()
    {
        List<SizeRequest> requests = [.. Enumerable.Range(0, 20).Select(i =>
        {
            _scratch.File($@"many\{i}\f.bin", i * 100);
            return new SizeRequest($"id{i}", Path.Combine(_scratch.Root, "many", i.ToString()));
        })];

        var results = new List<FolderSize>();
        await foreach (FolderSize size in _service.MeasureAllAsync(requests, TestContext.Current.CancellationToken))
        {
            results.Add(size);
        }

        Assert.Equal(requests.Select(r => r.Id).Order(), results.Select(r => r.Id).Order());
        Assert.All(results, r => Assert.Equal(int.Parse(r.Id[2..]) * 100, r.Bytes));
    }

    [Fact]
    public async Task Cancellation_stops_the_stream()
    {
        _scratch.File(@"c\f.bin", 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (FolderSize _ in _service.MeasureAllAsync([new SizeRequest("x", Path.Combine(_scratch.Root, "c"))], cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public void System_drive_has_a_known_volume()
    {
        VolumeInfo volume = new DiskKindProbe().Probe(Environment.SystemDirectory);

        Assert.StartsWith(@"\\?\Volume{", volume.Volume, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(DiskKind.Unknown, volume.Kind);
    }

    [Theory]
    [InlineData(DiskKind.Ssd, 4)]
    [InlineData(DiskKind.Hdd, 1)]
    [InlineData(DiskKind.Unknown, 2)]
    public void Workers_follow_the_disk_kind(DiskKind kind, int workers)
    {
        Assert.Equal(workers, DiskKindProbe.WorkersFor(kind));
    }
}
