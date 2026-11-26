using System.Text;
using Niskala.Tracker.Logs;

namespace Niskala.Tracker.Tests;

public sealed class InstallLogStoreTests
{
    [Fact]
    public void A_log_survives_a_round_trip_with_its_binary_data()
    {
        InstallLog log = Sample("20261121-100000-ab12") with
        {
            Values = [new ValueChange(@"HKCU\Software\Acme", "Blob", ChangeKind.Modified) { Kind = 3, Data = [0, 1, 255], PreviousKind = 3, PreviousData = [7] }],
            Filtered = new Dictionary<string, int> { ["Temporary files"] = 4 },
            Statistics = new TraceStatistics(1000, 40, 0, 0, 0, 1, 2),
        };

        using var stream = new MemoryStream();
        InstallLogStore.Write(stream, log);
        string text = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        InstallLog read = InstallLogStore.Read(stream);

        Assert.Contains("\"format\": \"nsklog\"", text, StringComparison.Ordinal);
        Assert.Contains("\"change\": \"Modified\"", text, StringComparison.Ordinal);
        Assert.Equal(log.Files, read.Files);
        Assert.Equal(log.Keys, read.Keys);
        Assert.Equal(log.Processes, read.Processes);
        ValueChange value = Assert.Single(read.Values);
        Assert.Equal([0, 1, 255], value.Data);
        Assert.Equal([7], value.PreviousData);
        Assert.Equal(4, read.Filtered["Temporary files"]);
        Assert.Equal(log.Statistics, read.Statistics);
    }

    [Fact]
    public void A_log_with_only_the_required_fields_reads_with_empty_lists()
    {
        const string Json = "{\"format\":\"nsklog\",\"schemaVersion\":1,\"id\":\"a\",\"name\":\"a\",\"installer\":\"a\",\"startedAt\":\"2026-11-21T10:00:00Z\"}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json));

        InstallLog log = InstallLogStore.Read(stream);

        Assert.Empty(log.Processes);
        Assert.Empty(log.Files);
        Assert.Empty(log.Keys);
        Assert.Empty(log.Values);
        Assert.Empty(log.FirewallRules);
        Assert.Empty(log.Filtered);
        Assert.Empty(log.Notes);
    }

    [Theory]
    [InlineData("{\"format\":\"other\",\"schemaVersion\":1,\"id\":\"a\",\"name\":\"a\",\"installer\":\"a\",\"startedAt\":\"2026-11-21T10:00:00Z\"}", "Not a Niskala install log")]
    [InlineData("{\"format\":\"nsklog\",\"schemaVersion\":9,\"id\":\"a\",\"name\":\"a\",\"installer\":\"a\",\"startedAt\":\"2026-11-21T10:00:00Z\"}", "schema 9 is not supported")]
    [InlineData("{\"format\":\"nsklog\",\"schemaVersion\":1}", "Not a readable install log")]
    [InlineData("not json", "Not a readable install log")]
    public void Anything_else_is_refused(string json, string message)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => InstallLogStore.Read(stream)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_lists_newest_first_skips_damaged_files_and_finds_an_app()
    {
        using var scratch = new ScratchFolder();
        var store = new InstallLogStore(scratch.Root);
        store.Save(Sample("20261120-090000-0001") with { StartedAt = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), AppId = "reg:hkcu:Acme" });
        store.Save(Sample("20261121-090000-0002") with { StartedAt = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero), AppId = "reg:hkcu:Acme", RolledBackAt = DateTimeOffset.UtcNow });
        File.WriteAllText(Path.Combine(scratch.Root, "broken" + InstallLogStore.Extension), "{");

        Assert.Equal(["20261121-090000-0002", "20261120-090000-0001"], store.List().Select(l => l.Id));
        Assert.Equal("20261120-090000-0001", store.FindForApp("REG:HKCU:ACME")?.Id);
        Assert.Null(store.FindForApp("reg:hkcu:Other"));
        Assert.True(store.Delete("20261120-090000-0001"));
        Assert.Null(store.Load("20261120-090000-0001"));
        Assert.Throws<ArgumentException>(() => store.Load(@"..\escape"));
        Assert.Matches("^[0-9]{8}-[0-9]{6}-[0-9a-f]{4}$", store.NewId());
    }

    private static InstallLog Sample(string id) => new()
    {
        Format = InstallLog.FormatName,
        SchemaVersion = InstallLogStore.SchemaVersion,
        Id = id,
        Name = "Acme",
        Installer = @"C:\Downloads\acme-setup.exe",
        StartedAt = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero),
        Processes = [new RecordedProcess(100, 50, @"C:\Downloads\acme-setup.exe", new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), false)],
        Files = [new FileChange(@"C:\Program Files\Acme", ChangeKind.Created, true)],
        Keys = [new KeyChange(@"HKLM\SYSTEM\CurrentControlSet\Services\Acme", ChangeKind.Created) { Origin = "service" }],
    };
}
