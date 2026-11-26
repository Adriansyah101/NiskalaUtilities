using Niskala.Toolkit.Wipe;

namespace Niskala.Toolkit.Tests;

public sealed class SecureWipeTests
{
    [Theory]
    [InlineData(WipeMethod.ZeroFill, 1)]
    [InlineData(WipeMethod.Dod522022M, 3)]
    [InlineData(WipeMethod.Gutmann, 35)]
    public void Each_standard_has_its_pass_count(WipeMethod method, int passes) => Assert.Equal(passes, WipePatterns.For(method).Count);

    [Fact]
    public void Gutmann_frames_its_fixed_patterns_with_random_passes()
    {
        IReadOnlyList<byte[]?> passes = WipePatterns.For(WipeMethod.Gutmann);

        Assert.All(passes.Take(4).Concat(passes.TakeLast(4)), Assert.Null);
        Assert.All(passes.Skip(4).Take(27), Assert.NotNull);
        Assert.Equal([0x55], passes[4]);
        Assert.Equal([0xDB, 0x6D, 0xB6], passes[30]);
        Assert.Equal([[0x00], [0xFF], null], WipePatterns.For(WipeMethod.Dod522022M));
    }

    [Fact]
    public void Three_byte_patterns_stay_in_phase_across_chunks()
    {
        using var scratch = new ScratchFolder();
        string path = scratch.File("pattern.bin", new byte[SecureWiper.ChunkBytes + 11]);
        byte[] pattern = [0x92, 0x49, 0x24];

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            SecureWiper.Overwrite(stream, stream.Length, pattern, path, 1, 1, null, TestContext.Current.CancellationToken);
        }

        byte[] written = File.ReadAllBytes(path);
        Assert.Equal(SecureWiper.ChunkBytes + 11, written.Length);
        Assert.True(written.Select((b, i) => b == pattern[i % 3]).All(ok => ok));
    }

    [Theory]
    [InlineData(WipeMethod.ZeroFill)]
    [InlineData(WipeMethod.Dod522022M)]
    [InlineData(WipeMethod.Gutmann)]
    public void A_wiped_file_is_gone_with_no_renamed_copy_left(WipeMethod method)
    {
        using var scratch = new ScratchFolder();
        string path = scratch.File(@"secret\plan.docx", Fixtures.Bytes(70_000, 8));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var passes = new HashSet<int>();

        WipeResult result = new SecureWiper(Fixtures.Guard()).WipeFile(path, method, new Inline(p => passes.Add(p.Pass)), TestContext.Current.CancellationToken);

        Assert.Equal((WipeOutcome.Wiped, 70_000L), (result.Outcome, result.Bytes));
        Assert.Equal(WipePatterns.For(method).Count, passes.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Protected_linked_shared_and_open_files_are_refused()
    {
        using var scratch = new ScratchFolder();
        string guardedFolder = Directory.CreateDirectory(scratch.Path("guarded")).FullName;
        string guarded = scratch.File(@"guarded\keep.txt", Fixtures.Bytes(10, 1));
        string shared = scratch.File(@"a\shared.bin", Fixtures.Bytes(10, 2));
        scratch.Link("/H", @"b\shared-link.bin", shared);
        string open = scratch.File(@"a\open.bin", Fixtures.Bytes(10, 3));
        var wiper = new SecureWiper(Fixtures.Guard(guardedFolder));

        WipeResult protectedResult = wiper.WipeFile(guarded, WipeMethod.ZeroFill, cancellationToken: TestContext.Current.CancellationToken);
        WipeResult linkResult = wiper.WipeFile(shared, WipeMethod.ZeroFill, cancellationToken: TestContext.Current.CancellationToken);
        WipeResult folderResult = wiper.WipeFile(scratch.Path("a"), WipeMethod.ZeroFill, cancellationToken: TestContext.Current.CancellationToken);
        WipeResult missingResult = wiper.WipeFile(scratch.Path("absent.bin"), WipeMethod.ZeroFill, cancellationToken: TestContext.Current.CancellationToken);
        WipeResult openResult;
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            openResult = wiper.WipeFile(open, WipeMethod.ZeroFill, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [WipeOutcome.Refused, WipeOutcome.Refused, WipeOutcome.Refused, WipeOutcome.Missing, WipeOutcome.InUse],
            new[] { protectedResult, linkResult, folderResult, missingResult, openResult }.Select(r => r.Outcome));
        Assert.Equal(Fixtures.Bytes(10, 1), File.ReadAllBytes(guarded));
        Assert.Equal(Fixtures.Bytes(10, 2), File.ReadAllBytes(shared));
        Assert.Equal(Fixtures.Bytes(10, 3), File.ReadAllBytes(open));
    }

    [Fact]
    public async Task Free_space_is_filled_pass_by_pass_and_the_fill_files_are_removed()
    {
        using var scratch = new ScratchFolder();
        string folder = scratch.Path("fill");
        var passes = new HashSet<int>();

        FreeSpaceResult result = await new SecureWiper(Fixtures.Guard()).WipeFreeSpaceAsync(
            scratch.Root,
            WipeMethod.Dod522022M,
            new FreeSpaceOptions { MaxBytes = 3L << 20, Folder = folder },
            new Inline(p => passes.Add(p.Pass)),
            TestContext.Current.CancellationToken);

        Assert.Equal((WipeOutcome.Wiped, 3, 3L << 20), (result.Outcome, result.Passes, result.Bytes));
        Assert.Equal([1, 2, 3], passes.Order());
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_cancelled_free_space_wipe_still_removes_its_files()
    {
        using var scratch = new ScratchFolder();
        string folder = scratch.Path("fill");
        using var cancel = new CancellationTokenSource();

        FreeSpaceResult result = await new SecureWiper(Fixtures.Guard()).WipeFreeSpaceAsync(
            scratch.Root,
            WipeMethod.Gutmann,
            new FreeSpaceOptions { MaxBytes = 8L << 20, Folder = folder },
            new Inline(p =>
            {
                if (p.Pass == 2)
                {
                    cancel.Cancel();
                }
            }),
            cancel.Token);

        Assert.Equal(WipeOutcome.Cancelled, result.Outcome);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task The_wipe_folder_must_be_new_and_allowed()
    {
        using var scratch = new ScratchFolder();
        string existing = Directory.CreateDirectory(scratch.Path("taken")).FullName;

        FreeSpaceResult taken = await new SecureWiper(Fixtures.Guard()).WipeFreeSpaceAsync(scratch.Root, WipeMethod.ZeroFill, new FreeSpaceOptions { MaxBytes = 1, Folder = existing }, cancellationToken: TestContext.Current.CancellationToken);
        FreeSpaceResult guarded = await new SecureWiper(Fixtures.Guard(scratch.Root)).WipeFreeSpaceAsync(scratch.Root, WipeMethod.ZeroFill, new FreeSpaceOptions { MaxBytes = 1, Folder = scratch.Path("new") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((WipeOutcome.Refused, WipeOutcome.Refused), (taken.Outcome, guarded.Outcome));
        Assert.True(Directory.Exists(existing));
        Assert.False(Directory.Exists(scratch.Path("new")));
    }

    private sealed class Inline(Action<WipeProgress> report) : IProgress<WipeProgress>
    {
        public void Report(WipeProgress value) => report(value);
    }
}
