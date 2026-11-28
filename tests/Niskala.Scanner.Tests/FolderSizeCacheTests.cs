using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Scanner.Sizing;

namespace Niskala.Scanner.Tests;

public sealed class FolderSizeCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_stored_size_is_served_while_the_folder_is_unchanged()
    {
        string folder = NewFolder("same");
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[10]);
        FolderSizeCache cache = Load(Path.Combine(NewFolder("same-cache"), "sizes.json"));
        int measured = 0;

        FolderSize first = cache.Measure(Request(folder), DiskKind.Ssd, CancellationToken.None, (r, k, _) => Size(r, k, 10, ref measured));
        FolderSize second = cache.Measure(Request(folder), DiskKind.Ssd, CancellationToken.None, (r, k, _) => Size(r, k, 99, ref measured));

        Assert.Equal(1, measured);
        Assert.Equal(10, first.Bytes);
        Assert.Equal(10, second.Bytes);
    }

    [Fact]
    public void A_new_file_in_the_folder_invalidates_the_entry()
    {
        string folder = NewFolder("grown");
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[10]);
        FolderSizeCache cache = Load(Path.Combine(NewFolder("grown-cache"), "sizes.json"));
        int measured = 0;
        cache.Measure(Request(folder), DiskKind.Ssd, CancellationToken.None, (r, k, _) => Size(r, k, 10, ref measured));

        File.WriteAllBytes(Path.Combine(folder, "b.bin"), new byte[5]);

        Assert.Null(cache.TryGet(Request(folder), DiskKind.Ssd));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_rewritten_entry_invalidates_the_stamp()
    {
        string folder = NewFolder("touched");
        string file = Path.Combine(folder, "a.bin");
        File.WriteAllBytes(file, new byte[10]);
        FolderStamp before = FolderStamp.Read(folder)!.Value;

        File.SetLastWriteTimeUtc(file, before.NewestEntryUtc.AddMinutes(5));

        Assert.NotEqual(before, FolderStamp.Read(folder));
    }

    [Fact]
    public void Entries_survive_a_save_and_reload()
    {
        string folder = NewFolder("saved");
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[3]);
        string file = Path.Combine(NewFolder("saved-cache"), "nested", "sizes.json");
        FolderSizeCache cache = Load(file);
        cache.Store(Size(Request(folder), DiskKind.Hdd, 42), FolderStamp.Read(folder)!.Value);
        cache.Save();

        FolderSizeCache reloaded = Load(file);

        Assert.Equal(42, reloaded.TryGet(Request(folder), DiskKind.Hdd)?.Bytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public void Entries_older_than_the_limit_are_dropped_on_load()
    {
        string folder = NewFolder("old");
        string file = Path.Combine(NewFolder("old-cache"), "sizes.json");
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FolderSizeCache cache = FolderSizeCache.Load(file, NullLogger.Instance, clock);
        cache.Store(Size(Request(folder), DiskKind.Ssd, 7), FolderStamp.Read(folder)!.Value);
        cache.Save();

        clock.Now += FolderSizeCache.MaxAge + TimeSpan.FromHours(1);

        Assert.Equal(0, FolderSizeCache.Load(file, NullLogger.Instance, clock).Count);
    }

    [Fact]
    public void A_damaged_file_starts_an_empty_cache()
    {
        string file = Path.Combine(NewFolder("broken"), "sizes.json");
        File.WriteAllText(file, "{ not json");

        Assert.Equal(0, Load(file).Count);
    }

    [Fact]
    public void Missing_links_and_failed_sizes_are_not_stored()
    {
        string folder = NewFolder("skipped");
        FolderSizeCache cache = Load(Path.Combine(NewFolder("skipped-cache"), "sizes.json"));
        FolderStamp stamp = FolderStamp.Read(folder)!.Value;

        cache.Store(new FolderSize("x", folder, Exists: false, IsLink: false, 1, 1, 0, DiskKind.Ssd, TimeSpan.Zero), stamp);
        cache.Store(new FolderSize("x", folder, Exists: true, IsLink: true, 1, 1, 0, DiskKind.Ssd, TimeSpan.Zero), stamp);
        cache.Store(new FolderSize("x", folder, Exists: true, IsLink: false, 1, 1, 0, DiskKind.Ssd, TimeSpan.Zero, "denied"), stamp);
        cache.Store(new FolderSize("x", "relative", Exists: true, IsLink: false, 1, 1, 0, DiskKind.Ssd, TimeSpan.Zero), stamp);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void The_queue_serves_cached_rows_without_measuring_them()
    {
        string folder = NewFolder("queued");
        FolderSizeCache cache = Load(Path.Combine(NewFolder("queued-cache"), "sizes.json"));
        cache.Store(Size(Request(folder), DiskKind.Ssd, 5), FolderStamp.Read(folder)!.Value);
        var seen = new ConcurrentQueue<FolderSize>();
        using var done = new ManualResetEventSlim();
        int measured = 0;
        using var queue = new SizeQueue(_ => new VolumeInfo("ssd", DiskKind.Ssd), (r, k, _) => Size(r, k, 9, ref measured), cache);
        queue.Measured += size =>
        {
            seen.Enqueue(size);
            done.Set();
        };

        queue.Enqueue(Request(folder));

        Assert.True(done.Wait(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref measured));
        Assert.Equal(5, Assert.Single(seen).Bytes);
    }

    private static FolderSizeCache Load(string file) => FolderSizeCache.Load(file, NullLogger.Instance);

    private static SizeRequest Request(string folder) => new("row", folder);

    private static FolderSize Size(SizeRequest request, DiskKind kind, long bytes) =>
        new(request.Id, request.Path, Exists: true, IsLink: false, bytes, 1, 0, kind, TimeSpan.Zero);

    private static FolderSize Size(SizeRequest request, DiskKind kind, long bytes, ref int measured)
    {
        Interlocked.Increment(ref measured);
        return Size(request, kind, bytes);
    }

    private static string NewFolder(string name)
    {
        string folder = Path.Combine(TestRoots.Folder, "folder-sizes", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
