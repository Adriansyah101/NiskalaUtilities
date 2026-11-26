using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using static Niskala.Guardian.Tests.Guards;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Guardian.Tests;

public sealed class CleanerTests
{
    [Fact]
    public async Task Removes_keys_values_files_and_rules_as_one_transaction_and_restores_them()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        string root = Path.GetDirectoryName(scratch.File(@"Programs\Acme\acme.exe"))!;
        using (RegistryKey key = registry.Create(@"Acme\Settings"))
        {
            key.SetValue("Theme", "dark");
        }

        using (RegistryKey run = registry.Create("Run"))
        {
            run.SetValue("Acme", $"\"{root}\\acme.exe\"");
            run.SetValue("Other", "other.exe");
        }

        var firewall = new FakeFirewall(FakeFirewall.Rule("Acme", Path.Combine(root, "acme.exe")), FakeFirewall.Rule("Acme", Path.Combine(root, "acme.exe")));
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        var points = new FakeRestorePoints();
        LeftoverItem[] items =
        [
            Item(LeftoverKind.Folder, root),
            Item(LeftoverKind.RegistryKey, registry.Path("Acme")),
            Item(LeftoverKind.RegistryValue, registry.Path("Run"), "Acme"),
            Item(LeftoverKind.FirewallRule, "Acme"),
        ];

        CleanResult result = await Cleaner(store, Real(), points: points, firewall: firewall)
            .CleanAsync(App(), Fingerprint(root), items, new CleanOptions { Permanent = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Transaction.Items, i => Assert.Equal(RemovalStatus.Removed, i.Status));
        Assert.Single(points.Requests);
        Assert.False(Directory.Exists(root));
        Assert.Empty(firewall.Rules);
        Assert.Null(Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme"));
        using (RegistryKey run = registry.Create("Run"))
        {
            Assert.Equal(["Other"], run.GetValueNames());
        }

        TransactionRecord saved = store.Load(result.Transaction.Id)!;
        Assert.Equal(result.Transaction.Items, saved.Items);
        Assert.True(saved.HasRegistryBackup);
        Assert.Equal(2, saved.FirewallRules.Count);
        Assert.Equal(42, saved.RestorePoint?.SequenceNumber);
        Assert.NotNull(saved.FinishedAt);

        RestoreReport report = new TransactionRestorer(store, firewall, new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(saved.Id)!;

        Assert.Equal(2, report.ValuesWritten);
        Assert.Equal(2, report.RulesRestored);
        Assert.Equal(2, firewall.Rules.Count);
        Assert.Contains(report.Notes, n => n.Contains("deleted permanently", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("#42", StringComparison.Ordinal));
        using (RegistryKey? settings = Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme\Settings"))
        {
            Assert.Equal("dark", settings?.GetValue("Theme"));
        }

        using (RegistryKey run = registry.Create("Run"))
        {
            Assert.Equal($"\"{root}\\acme.exe\"", run.GetValue("Acme"));
        }

        Assert.NotNull(store.Load(saved.Id)!.RestoredAt);
        Assert.Contains("already restored", Assert.Single(new TransactionRestorer(store, firewall, new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(saved.Id)!.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_that_fail_the_check_again_are_left_alone()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Programs\Acme\acme.exe"))!;
        string guarded = Path.GetDirectoryName(scratch.File(@"Guarded\Acme\data.bin"))!;
        var firewall = new FakeFirewall(FakeFirewall.Rule("Acme", Path.Combine(root, "acme.exe")), FakeFirewall.Rule("Acme", @"C:\Other\other.exe"));
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        LeftoverItem[] items =
        [
            Item(LeftoverKind.Folder, guarded),
            Item(LeftoverKind.RegistryKey, @"HKCU\Software"),
            Item(LeftoverKind.FirewallRule, "Acme"),
            Item(LeftoverKind.File, Path.Combine(root, "gone.txt")),
        ];

        CleanResult result = await Cleaner(store, Real(Path.Combine(scratch.Root, "Guarded")), firewall: firewall)
            .CleanAsync(App(), Fingerprint(root), items, new CleanOptions { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Transaction.Items, i => Assert.Equal(RemovalStatus.Skipped, i.Status));
        Assert.Contains("ProtectedTree", result.Transaction.Items[0].Detail, StringComparison.Ordinal);
        Assert.Contains("other programs", result.Transaction.Items[2].Detail, StringComparison.Ordinal);
        Assert.Equal("already gone", result.Transaction.Items[3].Detail);
        Assert.True(Directory.Exists(guarded));
        Assert.Equal(2, firewall.Rules.Count);
        Assert.Null(result.Transaction.RestorePoint);
    }

    [Fact]
    public async Task Recycled_items_come_back_from_the_Recycle_Bin()
    {
        using var scratch = new ScratchFolder();
        string folder = Path.GetDirectoryName(scratch.File(@"Roaming\Acme\settings.json", 32))!;
        string file = scratch.File(@"Local\acme.log", 16);
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));

        CleanResult result = await Cleaner(store, Real())
            .CleanAsync(App(), Fingerprint(), [Item(LeftoverKind.Folder, folder), Item(LeftoverKind.File, file)], new CleanOptions { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result.Transaction.Items, i =>
        {
            Assert.Equal(RemovalStatus.Removed, i.Status);
            Assert.Contains(@"\$Recycle.Bin\", i.RecycledPath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(file));
        string recycledFolder = result.Transaction.Items[0].RecycledPath!;
        Assert.True(Directory.Exists(recycledFolder));

        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(result.Transaction.Id)!;

        Assert.Empty(report.Problems);
        Assert.Equal(2, report.ItemsRestored);
        Assert.Equal(32, new FileInfo(Path.Combine(folder, "settings.json")).Length);
        Assert.Equal(16, new FileInfo(file).Length);
        Assert.False(Directory.Exists(recycledFolder));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(recycledFolder)!, "$I" + Path.GetFileName(recycledFolder)[2..])));
    }

    [Fact]
    public async Task Empty_publisher_folders_are_offered_removed_and_restored()
    {
        using var scratch = new ScratchFolder();
        string roaming = scratch.Dir("Roaming");
        string product = Path.GetDirectoryName(scratch.File(@"Roaming\Acme\Photo Editor\settings.json"))!;
        string publisher = Path.Combine(roaming, "Acme");
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        LeftoverCleaner cleaner = Cleaner(store, Real(), roots: [roaming]);
        LeftoverItem[] items = [Item(LeftoverKind.Folder, product)];

        Assert.Equal([publisher], cleaner.PredictEmptyParents(items));
        CleanResult result = await cleaner.CleanAsync(App(), Fingerprint(), items, new CleanOptions { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([publisher], result.EmptyParents);

        TransactionRecord updated = cleaner.RemoveEmptyFolders(result.Transaction.Id, result.EmptyParents);
        RemovalRecord removed = Assert.Single(updated.Items, i => i.EmptyFolder);
        Assert.Equal(RemovalStatus.Removed, removed.Status);
        Assert.False(Directory.Exists(publisher));
        Assert.True(Directory.Exists(roaming));

        new TransactionRestorer(store, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(updated.Id);
        Assert.True(Directory.Exists(publisher));
        Assert.False(Directory.Exists(product));
    }

    private static Fingerprint Fingerprint(params string[] roots) => new() { AppId = App().Id, TakenAt = DateTimeOffset.UtcNow, Roots = roots };
}
