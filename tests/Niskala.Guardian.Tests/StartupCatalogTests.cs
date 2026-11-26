using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Guardian.Startup;

namespace Niskala.Guardian.Tests;

public sealed class StartupCatalogTests
{
    [Fact]
    public void Entries_are_listed_with_the_state_Explorer_recorded()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        DateTimeOffset off = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        using (RegistryKey run = registry.Create("Run"))
        {
            run.SetValue("Acme", "\"C:\\Program Files\\Acme\\acme.exe\" --tray");
            run.SetValue("Paused", @"C:\Tools\paused.exe");
            run.SetValue(string.Empty, "ignored default value");
        }

        using (RegistryKey approved = registry.Create(@"Approved\Run"))
        {
            byte[] data = new byte[12];
            data[0] = 3;
            BitConverter.TryWriteBytes(data.AsSpan(4), off.ToFileTime());
            approved.SetValue("Paused", data, RegistryValueKind.Binary);
            approved.SetValue("Acme", new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        string folder = scratch.Dir("Startup");
        File.WriteAllText(Path.Combine(folder, "sync.cmd"), "@echo off");
        string ini = Path.Combine(folder, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        IReadOnlyList<StartupEntry> entries = Catalog(scratch, registry, folder).List();

        Assert.Equal(["Acme", "Paused", "sync.cmd"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        StartupEntry acme = entries.Single(e => e.Name == "Acme");
        Assert.True(acme.Enabled);
        Assert.Equal(@"C:\Program Files\Acme\acme.exe", acme.TargetPath);
        StartupEntry paused = entries.Single(e => e.Name == "Paused");
        Assert.False(paused.Enabled);
        Assert.Equal(off, paused.DisabledAt);
        StartupEntry sync = entries.Single(e => e.Name == "sync.cmd");
        Assert.Equal(Path.Combine(folder, "sync.cmd"), sync.Path);
        Assert.True(sync.Enabled);
    }

    [Fact]
    public async Task An_entry_is_switched_off_and_on_and_its_removal_can_be_restored()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        using (RegistryKey run = registry.Create("Run"))
        {
            run.SetValue("Acme", @"C:\Acme\acme.exe");
        }

        var time = new FixedTime(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero));
        var journal = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        StartupCatalog catalog = Catalog(scratch, registry, scratch.Dir("Startup"), journal, time);
        StartupEntry acme = Assert.Single(catalog.List());

        Assert.Equal(0, catalog.SetEnabled(acme, enabled: false));
        StartupEntry off = Assert.Single(catalog.List());
        Assert.False(off.Enabled);
        Assert.Equal(time.GetUtcNow(), off.DisabledAt);

        Assert.Equal(0, catalog.SetEnabled(off, enabled: true));
        Assert.True(Assert.Single(catalog.List()).Enabled);

        CleanResult result = await catalog.RemoveAsync(acme, TestContext.Current.CancellationToken);
        Assert.Empty(catalog.List());
        Assert.Equal(RemovalStatus.Removed, Assert.Single(result.Transaction.Items).Status);

        new TransactionRestorer(journal, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(result.Transaction.Id);
        Assert.Equal(@"C:\Acme\acme.exe", Assert.Single(catalog.List()).Command);
    }

    private static StartupCatalog Catalog(ScratchFolder scratch, RegistryScratch registry, string folder, TransactionStore? journal = null, TimeProvider? time = null) => new(
        [
            new StartupLocation(StartupKind.RunValue, InstallScope.User, false, registry.Path("Run"), registry.Path(@"Approved\Run")),
            new StartupLocation(StartupKind.StartupFolder, InstallScope.User, false, folder, registry.Path(@"Approved\StartupFolder")),
        ],
        Guards.Cleaner(journal ?? new TransactionStore(Path.Combine(scratch.Root, "journal")), Guards.Real()),
        SystemCommandEnvironment.Instance,
        time);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
