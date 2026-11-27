using System.Globalization;
using System.Text.Json;
using Niskala.Guardian.Journal;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class HistoryDataTests : IDisposable
{
    private const string BeforeSettings = """
        {"schemaVersion":1,"id":"20261122-023400-8134","kind":"Uninstall","appId":"reg:hklm64:Acme_is1","appName":"Acme","startedAt":"2026-11-22T02:34:00+00:00","method":"RecycleBin","hasRegistryBackup":true,"firewallRules":[],"items":[{"kind":"Folder","path":"C:\\Acme","status":"Removed"}],"notes":[]}
        """;

    private const string RequiredOnly = """
        {"schemaVersion":1,"id":"20261121-101010-0001","kind":"Clean","appId":"reg:hkcu:Acme","startedAt":"2026-11-21T10:10:10+00:00"}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"niskala-tests-{Environment.ProcessId}-history"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void History_rows_build_from_journals_written_by_older_versions()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        foreach ((string id, string json) in new[] { ("20261122-023400-8134", BeforeSettings), ("20261121-101010-0001", RequiredOnly) })
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, id)).FullName;
            File.WriteAllText(Path.Combine(folder, "transaction.json"), json);
        }

        TransactionItem[] rows = [.. new TransactionStore(_root).List().Select(r => new TransactionItem(r))];

        Assert.Equal(["Acme", "reg:hkcu:Acme"], rows.Select(r => r.Title));
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.Counts));
            Assert.False(string.IsNullOrEmpty(row.Kind));
            Assert.False(string.IsNullOrEmpty(row.When));
            Assert.True(row.CanRestore);
        });
        Assert.Equal(1, rows[0].Record.Items.Count + rows[0].Record.Settings.Count);
    }
}
