using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Settings;

namespace Niskala.Guardian.Tests;

public sealed class JournalCompatibilityTests
{
    private const string BeforeSettings = """
        {"schemaVersion":1,"id":"20261122-023400-8134","kind":"Uninstall","appId":"reg:hklm64:Acme_is1","appName":"Acme","startedAt":"2026-11-22T02:34:00+00:00","method":"RecycleBin","hasRegistryBackup":true,"firewallRules":[],"items":[],"notes":["kept"]}
        """;

    private const string RequiredOnly = """
        {"schemaVersion":1,"id":"20261121-101010-0001","kind":"Clean","appId":"reg:hkcu:Acme","startedAt":"2026-11-21T10:10:10+00:00"}
        """;

    private static TransactionStore StoreWith(ScratchFolder scratch, params (string Id, string Json)[] records)
    {
        foreach ((string id, string json) in records)
        {
            string folder = Directory.CreateDirectory(Path.Combine(scratch.Root, id)).FullName;
            File.WriteAllText(Path.Combine(folder, "transaction.json"), json);
        }

        return new TransactionStore(scratch.Root);
    }

    [Fact]
    public void Json_reflection_is_off_so_only_generated_metadata_reads_the_journal()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<TransactionRecord>(RequiredOnly));
    }

    [Fact]
    public void A_record_written_before_settings_existed_loads_with_empty_lists()
    {
        using var scratch = new ScratchFolder();
        TransactionStore store = StoreWith(scratch, ("20261122-023400-8134", BeforeSettings));

        TransactionRecord record = store.Load("20261122-023400-8134")!;

        Assert.NotNull(record.Settings);
        Assert.Empty(record.Settings);
        Assert.NotNull(record.ScheduledTasks);
        Assert.Empty(record.ScheduledTasks);
        Assert.Empty(record.Items);
        Assert.Equal(["kept"], record.Notes);
        Assert.Equal(0, record.Items.Count + record.Settings.Count(s => s.Status == SettingStatus.Applied));
    }

    [Fact]
    public void Records_with_only_required_fields_list_without_null_lists()
    {
        using var scratch = new ScratchFolder();
        TransactionStore store = StoreWith(scratch, ("20261122-023400-8134", BeforeSettings), ("20261121-101010-0001", RequiredOnly));

        IReadOnlyList<TransactionRecord> records = store.List();

        Assert.Equal(["20261122-023400-8134", "20261121-101010-0001"], records.Select(r => r.Id));
        Assert.All(records, r =>
        {
            Assert.NotNull(r.FirewallRules);
            Assert.NotNull(r.ScheduledTasks);
            Assert.NotNull(r.Items);
            Assert.NotNull(r.Settings);
            Assert.NotNull(r.Notes);
        });
    }

    [Fact]
    public void A_damaged_record_is_skipped_and_the_rest_still_list()
    {
        using var scratch = new ScratchFolder();
        TransactionStore store = StoreWith(scratch, ("20261122-023400-8134", BeforeSettings), ("20261120-000000-dead", "{\"schemaVersion\":1,\"id\":"));

        Assert.Equal("20261122-023400-8134", Assert.Single(store.List()).Id);
    }

    [Fact]
    public void A_registry_backup_without_entries_loads_empty()
    {
        using var scratch = new ScratchFolder();
        TransactionStore store = StoreWith(scratch, ("20261122-023400-8134", BeforeSettings));
        using (FileStream file = File.Create(Path.Combine(scratch.Root, "20261122-023400-8134", "registry.json.gz")))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        {
            zip.Write(Encoding.UTF8.GetBytes("{}"));
        }

        Assert.Empty(store.LoadRegistryBackup("20261122-023400-8134")!.Entries);
    }
}
