using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Guardian.Restore;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Guardian.Tests;

public sealed class JournalTests
{
    [Fact]
    public void Records_and_compressed_backups_round_trip_and_list_newest_first()
    {
        using var scratch = new ScratchFolder();
        var store = new TransactionStore(scratch.Root);
        var older = new TransactionRecord
        {
            SchemaVersion = TransactionStore.SchemaVersion,
            Id = store.NewId(),
            Kind = TransactionKind.Clean,
            AppId = "reg:hkcu:Acme",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Items = [new RemovalRecord(LeftoverKind.Folder, @"C:\Acme", null, RemovalStatus.Removed) { RecycledPath = @"C:\$Recycle.Bin\S-1\$RABC" }],
        };
        TransactionRecord newer = older with { Id = store.NewId() + "b", StartedAt = DateTimeOffset.UtcNow };
        var backup = new RegistryBackup([new RegistryBackupEntry(@"HKCU\Software\Acme", new RegistryKeyBackup("Acme", [new RegistryValueBackup("A", 3, [1, 2])], []), null)]);

        store.Save(older);
        store.Save(newer);
        store.SaveRegistryBackup(older.Id, backup);

        Assert.Equal([newer.Id, older.Id], store.List().Select(r => r.Id));
        Assert.Equal(older.Items, store.Load(older.Id)!.Items);
        RegistryValueBackup value = Assert.Single(store.LoadRegistryBackup(older.Id)!.Entries[0].Key!.Values);
        Assert.Equal([1, 2], value.Data);
        Assert.Null(store.LoadRegistryBackup(newer.Id));
        Assert.Empty(Directory.GetFiles(scratch.Root, "*.tmp", SearchOption.AllDirectories));
        Assert.Throws<ArgumentException>(() => store.Load(@"..\escape"));
    }

    [Fact]
    public void Empty_parents_are_the_topmost_folders_below_a_root_that_held_only_removed_items()
    {
        using var scratch = new ScratchFolder();
        string roaming = scratch.Dir("Roaming");
        string programs = scratch.Dir("Programs");
        string nested = Path.GetDirectoryName(scratch.File(@"Roaming\Acme\Suite\App\a.txt"))!;
        string kept = Path.GetDirectoryName(scratch.File(@"Roaming\Vendor\App\a.txt"))!;
        scratch.File(@"Roaming\Vendor\shared.txt");
        string direct = Path.GetDirectoryName(scratch.File(@"Roaming\Solo\a.txt"))!;
        string first = Path.GetDirectoryName(scratch.File(@"Programs\Pub\One\a.exe"))!;
        string second = Path.GetDirectoryName(scratch.File(@"Programs\Pub\Two\b.exe"))!;
        string guarded = Path.GetDirectoryName(scratch.File(@"Programs\Guarded\App\c.exe"))!;
        var finder = new EmptyParentFinder([roaming, programs], Guards.Real(Path.Combine(programs, "Guarded")));

        IReadOnlyList<string> found = finder.Find([nested, kept, direct, first, second, guarded]);

        Assert.Equal([Path.Combine(programs, "Pub"), Path.Combine(roaming, "Acme")], found);
    }

    [Fact]
    public async Task The_uninstall_checkpoint_backs_up_the_entry_and_the_autostart_value()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        using (RegistryKey entry = registry.Create(@"Uninstall\Acme"))
        {
            entry.SetValue("DisplayName", "Acme");
        }

        using (RegistryKey run = registry.Create("Run"))
        {
            run.SetValue("Acme", "acme.exe");
        }

        var store = new TransactionStore(scratch.Root);
        InstalledApp app = Guards.App("Acme") with { RegistryKey = registry.Path(@"Uninstall\Acme") };
        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = DateTimeOffset.UtcNow,
            RunEntries = [new RegistryValueTrace(registry.Path("Run"), "Acme", "acme.exe")],
        };
        var guardian = new GuardianService(store, new FakeRestorePoints(), NullLogger<GuardianService>.Instance);

        GuardianCheckpoint checkpoint = await guardian.PrepareAsync(app, fingerprint, TestContext.Current.CancellationToken);

        Assert.True(checkpoint.CanRollBack);
        TransactionRecord saved = store.Load(checkpoint.TransactionId!)!;
        Assert.Equal(TransactionKind.Uninstall, saved.Kind);
        Assert.Equal(2, store.LoadRegistryBackup(saved.Id)!.Entries.Count);

        Hives.CurrentUser.DeleteSubKeyTree($@"{registry.Key}\Uninstall\Acme");
        using (RegistryKey run = registry.Create("Run"))
        {
            run.DeleteValue("Acme");
        }

        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(saved.Id)!;

        Assert.Equal(2, report.ValuesWritten);
        Assert.Contains(report.Notes, n => n.Contains("Only the registry backup", StringComparison.Ordinal));
        using RegistryKey? restored = Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Uninstall\Acme");
        Assert.Equal("Acme", restored?.GetValue("DisplayName"));
    }
}
