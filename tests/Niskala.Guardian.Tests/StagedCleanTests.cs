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

public sealed class StagedCleanTests
{
    [Fact]
    public async Task The_registry_stage_joins_the_journal_of_the_file_stage_and_both_restore()
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
        }

        var firewall = new FakeFirewall(FakeFirewall.Rule("Acme", Path.Combine(root, "acme.exe")));
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        LeftoverCleaner cleaner = Cleaner(store, Real(), firewall: firewall);
        Fingerprint fingerprint = Fingerprint(root);

        CleanResult files = await cleaner.CleanAsync(
            App(), fingerprint, [Item(LeftoverKind.Folder, root)], new CleanOptions { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);
        CleanResult keys = await cleaner.CleanAsync(
            App(),
            fingerprint,
            [Item(LeftoverKind.RegistryKey, registry.Path("Acme")), Item(LeftoverKind.RegistryValue, registry.Path("Run"), "Acme"), Item(LeftoverKind.FirewallRule, "Acme")],
            new CleanOptions { CreateRestorePoint = false, Into = files.Transaction.Id },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(files.Transaction.Id, keys.Transaction.Id);
        TransactionRecord saved = Assert.Single(store.List());
        Assert.Equal(4, saved.Items.Count);
        Assert.All(saved.Items, i => Assert.Equal(RemovalStatus.Removed, i.Status));
        Assert.Equal(RemovalMethod.Permanent, saved.Method);
        Assert.True(saved.HasRegistryBackup);
        Assert.Single(saved.FirewallRules);
        Assert.NotNull(saved.FinishedAt);
        Assert.False(Directory.Exists(root));

        RestoreReport report = new TransactionRestorer(store, firewall, new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(saved.Id)!;

        Assert.Equal(2, report.ValuesWritten);
        Assert.Equal(1, report.RulesRestored);
        Assert.Contains(report.Notes, n => n.Contains("deleted permanently", StringComparison.Ordinal));
        using (RegistryKey? settings = Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme\Settings"))
        {
            Assert.Equal("dark", settings?.GetValue("Theme"));
        }

        using (RegistryKey run = registry.Create("Run"))
        {
            Assert.Equal($"\"{root}\\acme.exe\"", run.GetValue("Acme"));
        }
    }

    [Fact]
    public async Task A_second_stage_keeps_the_registry_backup_of_the_first()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        using (RegistryKey run = registry.Create("Run"))
        {
            run.SetValue("Acme", "acme.exe");
        }

        using (RegistryKey key = registry.Create("Acme"))
        {
            key.SetValue("Theme", "dark");
        }

        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        LeftoverCleaner cleaner = Cleaner(store, Real());
        var options = new CleanOptions { CreateRestorePoint = false };

        CleanResult first = await cleaner.CleanAsync(App(), Fingerprint(), [Item(LeftoverKind.RegistryValue, registry.Path("Run"), "Acme")], options, cancellationToken: TestContext.Current.CancellationToken);
        await cleaner.CleanAsync(App(), Fingerprint(), [Item(LeftoverKind.RegistryKey, registry.Path("Acme"))], options with { Into = first.Transaction.Id }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, store.LoadRegistryBackup(first.Transaction.Id)!.Entries.Count);
        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(first.Transaction.Id)!;

        Assert.Equal(2, report.ValuesWritten);
        using (RegistryKey run = registry.Create("Run"))
        {
            Assert.Equal("acme.exe", run.GetValue("Acme"));
        }

        using (RegistryKey key = registry.Create("Acme"))
        {
            Assert.Equal("dark", key.GetValue("Theme"));
        }
    }

    [Fact]
    public async Task Joining_a_journal_that_does_not_exist_is_refused()
    {
        using var scratch = new ScratchFolder();
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Cleaner(store, Real()).CleanAsync(
            App(), Fingerprint(), [], new CleanOptions { CreateRestorePoint = false, Into = "20260101-000000-dead" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(store.List());
    }

    private static Fingerprint Fingerprint(params string[] roots) => new() { AppId = App().Id, TakenAt = DateTimeOffset.UtcNow, Roots = roots };
}
