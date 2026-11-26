using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Interop.Firewall;
using Niskala.Toolkit.Duplicates;

namespace Niskala.Toolkit.Tests;

public sealed class DuplicateTests
{
    private static readonly DateTime Older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Three_stages_separate_true_copies_from_look_alikes()
    {
        using var scratch = new ScratchFolder();
        byte[] large = Fixtures.Bytes(20_000, 1);
        byte[] lookAlike = (byte[])large.Clone();
        lookAlike[10_000] ^= 0xFF;
        scratch.File(@"a\photo.jpg", large, Older);
        scratch.File(@"b\photo (1).jpg", large, Newer);
        scratch.File(@"c\photo-edit.jpg", lookAlike);
        scratch.File(@"a\note.txt", Fixtures.Bytes(500, 9));
        scratch.File(@"b\note copy.txt", Fixtures.Bytes(500, 9));
        scratch.File(@"c\other.txt", Fixtures.Bytes(500, 3));
        scratch.File(@"c\single.bin", Fixtures.Bytes(77, 3));

        DuplicateReport report = await new DuplicateFinder(Fixtures.Guard()).FindAsync([scratch.Root], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, report.Scanned);
        Assert.Equal(6, report.SameSize);
        Assert.Equal(2, report.Groups.Count);
        DuplicateGroup photos = report.Groups[0];
        Assert.Equal((20_000L, 20_000L), (photos.Bytes, photos.Reclaimable));
        Assert.Equal(["photo.jpg", "photo (1).jpg"], photos.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(["note copy.txt", "note.txt"], report.Groups[1].Files.Select(f => Path.GetFileName(f.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Hard_links_are_one_file_not_a_duplicate()
    {
        using var scratch = new ScratchFolder();
        string original = scratch.File(@"a\data.bin", Fixtures.Bytes(9000, 5));
        scratch.Link("/H", @"b\data-link.bin", original);

        DuplicateReport report = await new DuplicateFinder(Fixtures.Guard()).FindAsync([scratch.Root], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(report.Groups);
        Assert.Equal(1, report.HardLinks);
    }

    [Fact]
    public async Task Protected_copies_are_shown_but_never_offered_for_removal()
    {
        using var scratch = new ScratchFolder();
        string guardedFolder = Directory.CreateDirectory(scratch.Path("guarded")).FullName;
        scratch.File(@"guarded\setup.dat", Fixtures.Bytes(3000, 2), Newer);
        scratch.File(@"free\setup.dat", Fixtures.Bytes(3000, 2), Older);

        DuplicateReport report = await new DuplicateFinder(Fixtures.Guard(guardedFolder)).FindAsync([scratch.Root], cancellationToken: TestContext.Current.CancellationToken);

        DuplicateGroup group = Assert.Single(report.Groups);
        Assert.Contains(group.Files, f => f.IsProtected);
        Assert.Empty(DuplicateSelection.Removable(group, KeepRule.Oldest));
        Assert.Equal(["setup.dat"], DuplicateSelection.Removable(group, KeepRule.Newest).Select(f => Path.GetFileName(f.Path)));
        Assert.Contains(@"\free\", DuplicateSelection.Removable(group, KeepRule.Newest)[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hidden_files_are_skipped_unless_asked_for()
    {
        using var scratch = new ScratchFolder();
        string hidden = scratch.File(@"a\copy.bin", Fixtures.Bytes(1000, 4));
        scratch.File(@"b\copy.bin", Fixtures.Bytes(1000, 4));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var finder = new DuplicateFinder(Fixtures.Guard());

        DuplicateReport plain = await finder.FindAsync([scratch.Root], cancellationToken: TestContext.Current.CancellationToken);
        DuplicateReport all = await finder.FindAsync([scratch.Root], new DuplicateOptions { IncludeHidden = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(plain.Groups);
        Assert.Single(all.Groups);
    }

    [Theory]
    [InlineData(KeepRule.Oldest, "old.bin")]
    [InlineData(KeepRule.Newest, "new.bin")]
    public void The_keep_rule_picks_by_last_write(KeepRule rule, string kept)
    {
        var group = new DuplicateGroup("h", 10,
        [
            new DuplicateFile(@"C:\x\mid.bin", 10, Older.AddDays(100), Older),
            new DuplicateFile(@"C:\x\old.bin", 10, Older, Older),
            new DuplicateFile(@"C:\x\new.bin", 10, Newer, Newer),
        ]);

        Assert.Equal(kept, Path.GetFileName(DuplicateSelection.Keeper(group, rule).Path));
        Assert.Equal(2, DuplicateSelection.Removable(group, rule).Count);
    }

    [Fact]
    public async Task Removal_goes_through_the_journal_skips_changed_copies_and_can_be_restored()
    {
        using var scratch = new ScratchFolder();
        string keep = scratch.File(@"a\clip.mov", Fixtures.Bytes(12_000, 6), Older);
        string copy = scratch.File(@"b\clip.mov", Fixtures.Bytes(12_000, 6), Newer);
        string edited = scratch.File(@"c\clip.mov", Fixtures.Bytes(12_000, 6), Newer.AddDays(1));
        DuplicateReport report = await new DuplicateFinder(Fixtures.Guard()).FindAsync([scratch.Root], cancellationToken: TestContext.Current.CancellationToken);
        byte[] changed = Fixtures.Bytes(12_000, 6);
        changed[0] ^= 1;
        File.WriteAllBytes(edited, changed);
        var store = new TransactionStore(scratch.Path("journal"));

        DuplicateRemoval removal = await new DuplicateCleaner(Fixtures.Cleaner(store, Fixtures.Guard()))
            .RemoveAsync(report.Groups, KeepRule.Oldest, permanent: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(edited));
        Assert.Equal([copy], removal.Removed.Select(f => f.Path));
        Assert.Equal([edited], removal.Changed.Select(f => f.Path));
        Assert.Equal(TransactionKind.DuplicateClean, removal.Result!.Transaction.Kind);

        RestoreReport restored = new TransactionRestorer(store, new NoFirewall(), new NoTasks(), NullLogger<TransactionRestorer>.Instance).Restore(removal.Result.Transaction.Id)!;

        Assert.Empty(restored.Problems);
        Assert.True(File.Exists(copy));
    }

    private sealed class NoFirewall : Guardian.IFirewallEditor
    {
        public int TryList(out List<FirewallRule> rules)
        {
            rules = [];
            return 0;
        }

        public int TryRemove(string name) => 0;

        public int TryAdd(FirewallRule rule) => 0;
    }

    private sealed class NoTasks : Guardian.IScheduledTaskEditor
    {
        public int TryRead(string path, out string? xml)
        {
            xml = null;
            return 0;
        }

        public int TryDelete(string path) => 0;

        public int TryRegister(string path, string xml) => 0;
    }
}
