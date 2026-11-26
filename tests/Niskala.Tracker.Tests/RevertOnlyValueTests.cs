using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Rollback;

namespace Niskala.Tracker.Tests;

public sealed class RevertOnlyValueTests
{
    private const int RegSz = 1;
    private const int RegExpandSz = 2;
    private const int RegDword = 4;

    [Fact]
    public void A_revert_only_value_stays_in_the_log_and_other_protected_values_do_not()
    {
        using var registry = new RegistryScratch();
        RegistryGuard guard = Guard(registry);
        var filter = new NoiseFilter([], [], Guards.Real(), guard);
        ValueChange[] values = [new(registry.Path("Env"), "Path", ChangeKind.Modified), new(registry.Path("Env"), "Locked", ChangeKind.Modified)];

        FilteredChanges result = filter.Apply(new ReconciledChanges([], [], values));

        ValueChange kept = Assert.Single(result.Changes.Values);
        Assert.Equal("Path", kept.Name);
        Assert.True(kept.RevertOnly);
        Assert.Equal(1, result.Filtered["Protected (ProtectedValue)"]);
    }

    [Fact]
    public async Task A_search_path_is_written_back_with_its_earlier_type_and_never_removed()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        string env = registry.Path("Env");
        using (RegistryKey key = registry.Create("Env"))
        {
            key.SetValue("Path", @"C:\Tools;C:\Acme", RegistryValueKind.String);
            key.SetValue("Mode", 2, RegistryValueKind.DWord);
            key.SetValue("Fresh", "acme", RegistryValueKind.String);
        }

        InstallLog log = new()
        {
            Format = InstallLog.FormatName,
            SchemaVersion = InstallLogStore.SchemaVersion,
            Id = "20261121-120000-0001",
            Name = "Acme",
            Installer = @"C:\Downloads\acme-setup.exe",
            StartedAt = DateTimeOffset.UtcNow,
            Values =
            [
                new(env, "Path", ChangeKind.Modified) { Kind = RegSz, Data = Text(@"C:\Tools;C:\Acme"), PreviousKind = RegExpandSz, PreviousData = Text(@"%USERPROFILE%\Tools"), RevertOnly = true },
                new(env, "Mode", ChangeKind.Modified) { Kind = RegDword, Data = BitConverter.GetBytes(2), PreviousKind = RegDword, PreviousData = BitConverter.GetBytes(1), RevertOnly = true },
                new(env, "Fresh", ChangeKind.Created) { Kind = RegSz, Data = Text("acme"), RevertOnly = true },
            ],
        };

        var store = new InstallLogStore(Path.Combine(scratch.Root, "logs"));
        store.Save(log);
        var journal = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        var rollback = new LogRollback(Guards.Cleaner(journal, Guards.Real(), registryGuard: Guard(registry)), store, LiveSystemState.Instance, NullLogger<LogRollback>.Instance);

        RollbackPlan plan = rollback.Plan(log);

        Assert.Empty(plan.Items);
        Assert.Equal(["Path", "Mode"], plan.Reverts.Select(r => r.Previous.Name));
        Assert.Contains(plan.Kept, k => k.Contains("\"Fresh\"", StringComparison.Ordinal) && k.Contains("never removed", StringComparison.Ordinal));

        CleanResult result = await rollback.ExecuteAsync(plan, new CleanOptions { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        using (RegistryKey key = registry.Create("Env"))
        {
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
            Assert.Equal(@"%USERPROFILE%\Tools", key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(2, key.GetValue("Mode"));
            Assert.Equal("acme", key.GetValue("Fresh"));
        }

        Assert.Equal(RemovalStatus.Reverted, result.Transaction.Items.Single(i => i.ValueName == "Path").Status);
        RemovalRecord mode = result.Transaction.Items.Single(i => i.ValueName == "Mode");
        Assert.Equal(RemovalStatus.Skipped, mode.Status);
        Assert.Contains(nameof(RegistryVerdict.RevertNotString), mode.Detail, StringComparison.Ordinal);
    }

    private static RegistryGuard Guard(RegistryScratch registry) => Guards.Registry(
        new ProtectedRegistryValue(RegistryRoot.User, $@"{registry.Key}\Env", "Path", "test search path", ContentRevertible: true),
        new ProtectedRegistryValue(RegistryRoot.User, $@"{registry.Key}\Env", "Mode", "test mode", ContentRevertible: true),
        new ProtectedRegistryValue(RegistryRoot.User, $@"{registry.Key}\Env", "Fresh", "test variable", ContentRevertible: true),
        new ProtectedRegistryValue(RegistryRoot.User, $@"{registry.Key}\Env", "Locked", "test variable"));

    private static byte[] Text(string text) => Encoding.Unicode.GetBytes(text + "\0");
}
