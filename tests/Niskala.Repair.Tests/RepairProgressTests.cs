using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Repair;
using Niskala.Repair.Output;

namespace Niskala.Repair.Tests;

public sealed class RepairProgressTests : IDisposable
{
    private const string ChkdskScan =
        "The type of the file system is NTFS.\r\n\r\n" +
        "Stage 1: Examining basic file system structure ...\r\n" +
        "  Progress: 0 of 812544 done; Stage:  0%; Total:  0%; ETA:   0:01:10    \r" +
        "  Progress: 406272 of 812544 done; Stage: 50%; Total: 16%; ETA:   0:00:40    \r" +
        "  812544 file records processed.\r\n" +
        "Stage 2: Examining file name linkage ...\r\n" +
        "  Progress: 900000 of 1100000 done; Stage: 81%; Total: 47%; ETA:   0:00:21 .  \r" +
        "  1100000 index entries processed.\r\n" +
        "Stage 3: Examining security descriptors ...\r\n" +
        "  Progress: 812544 of 812544 done; Stage: 100%; Total: 100%; ETA:   0:00:00 ...\r" +
        "Windows has scanned the file system and found no problems.\r\n";

    private const string ChkdskScanIndonesian =
        "Tahap 1: Memeriksa struktur sistem file dasar ...\r\n" +
        "  Kemajuan: 1000 dari 5000 selesai; Tahap: 20%; Total: 7%; ETA: 0:00:30 .\r" +
        "  Kemajuan: 5000 dari 5000 selesai; Tahap: 100%; Total: 33%; ETA: 0:00:12 ..\r" +
        "  5000 rekaman file diproses.\r\n";

    private const string DismIndonesian =
        "Alat Deployment Image Servicing and Management\r\nVersi: 10.0.26100.1\r\n\r\n" +
        "\r[===                        5,0%                           ] " +
        "\r[=============              42,0%                          ] " +
        "\r[==========================100,0%==========================] \r\n" +
        "Operasi pemulihan berhasil diselesaikan.\r\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("niskala-progress-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("Verification 42% complete.", 42.0)]
    [InlineData("Verifikasi 42% selesai.", 42.0)]
    [InlineData("[=== 42.0% ===]", 42.0)]
    [InlineData("[===                        42,0%                          ]", 42.0)]
    [InlineData("  Progress: 406272 of 812544 done; Stage: 50%; Total: 16%; ETA:   0:00:40    ", 16.0)]
    [InlineData("  Kemajuan: 1000 dari 5000 selesai; Tahap: 20%; Total: 7%; ETA: 0:00:30 .", 7.0)]
    [InlineData("Verification 0% complete.", 0.0)]
    [InlineData("Verification 100% complete.", 100.0)]
    public void Windows_progress_lines_give_their_percentage(string text, double expected)
    {
        Assert.Equal(expected, RepairProgressParser.TryParse(text));
    }

    [Theory]
    [InlineData("Stage 1: Examining basic file system structure ...")]
    [InlineData("  812544 file records processed.")]
    [InlineData("Windows IP Configuration")]
    [InlineData("Successfully flushed the DNS Resolver Cache.")]
    [InlineData("Sucessfully reset the Winsock Catalog.")]
    [InlineData("Versi: 10.0.26100.1")]
    public void Lines_without_progress_give_no_percentage(string text)
    {
        Assert.Null(RepairProgressParser.TryParse(text));
    }

    [Fact]
    public void Chkdsk_stages_are_split_on_carriage_returns_and_read_as_the_total()
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        foreach (string write in Writes(ChkdskScan))
        {
            assembler.Append(write, lines);
        }

        Assert.Null(assembler.Complete());
        Assert.Equal([0.0, 16, 47, 100], Percents(lines));
        Assert.Contains(lines, l => l is { IsFinal: true, Text: "  812544 file records processed." });
        Assert.Contains(lines, l => l is { IsFinal: true, Text: "Windows has scanned the file system and found no problems." });
        Assert.DoesNotContain(lines, l => l.IsFinal && l.Text.Contains("Progress:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    public void Chkdsk_progress_cut_into_any_chunks_still_ends_on_the_total(int chunk)
    {
        List<ConsoleLine> lines = Assemble(ChkdskScan, chunk);
        double[] percents = [.. lines.Select(l => RepairProgressParser.TryParse(l.Text)).OfType<double>()];
        Assert.Superset(new HashSet<double> { 16, 47, 100 }, new HashSet<double>(percents));
        Assert.Equal(100, percents[^1]);
        Assert.Contains(lines, l => l is { IsFinal: true, Text: "Windows has scanned the file system and found no problems." });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Indonesian_chkdsk_and_dism_output_is_read_the_same_way(int chunk)
    {
        Assert.Equal([7.0, 33], Read(Assemble(ChkdskScanIndonesian, chunk)).Distinct());
        Assert.Equal([5.0, 42, 100], Read(Assemble(DismIndonesian, chunk)).Distinct());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(4096)]
    public void A_line_cut_before_its_total_never_shows_the_stage_value(int chunk)
    {
        Assert.Equal([0.0, 16, 47, 100], Read(Assemble(ChkdskScan, chunk)).Distinct());
    }

    [Fact]
    public void A_redrawn_line_is_closed_as_soon_as_its_carriage_return_arrives()
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        assembler.Append("\r[==   4.0%   ] ", lines);
        assembler.Append("\r", lines);
        Assert.Equal([new ConsoleLine("[==   4.0%   ] ", false, IsOpen: true), new ConsoleLine("[==   4.0%   ] ", false)], lines);
        assembler.Append("[===  9.0%   ] ", lines);
        Assert.Equal(new ConsoleLine("[===  9.0%   ] ", false, IsOpen: true), lines[^1]);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void An_open_line_is_read_once_a_closed_one_showed_its_shape()
    {
        var reader = new ProgressReader();
        Assert.Null(reader.Read(new ConsoleLine("[=====      41.0%     ] ", IsFinal: false, IsOpen: true)));
        Assert.Equal(41, reader.Read(new ConsoleLine("[=====      41.0%     ] ", IsFinal: false)));
        Assert.Null(reader.Read(new ConsoleLine("[=====      42", IsFinal: false, IsOpen: true)));
        Assert.Equal(42, reader.Read(new ConsoleLine("[=====      42.0%     ] ", IsFinal: false, IsOpen: true)));
        Assert.Equal(100, reader.Read(new ConsoleLine("[==========================100.0%==========================]", IsFinal: true)));
    }

    [Fact]
    public void A_shorter_open_line_waits_until_it_is_closed()
    {
        var reader = new ProgressReader();
        Assert.Equal(16, reader.Read(new ConsoleLine("Progress: 1 of 2 done; Stage: 50%; Total: 16%; ETA: 0:00:40", IsFinal: false)));
        Assert.Null(reader.Read(new ConsoleLine("Progress: 2 of 2 done; Stage: 90%; To", IsFinal: false, IsOpen: true)));
        Assert.Equal(30, reader.Read(new ConsoleLine("Progress: 2 of 2 done; Stage: 90%; Total: 30%", IsFinal: false, IsOpen: true)));
        Assert.Equal(31, reader.Read(new ConsoleLine("Progress: 2 of 2 done; Stage: 91%; Total: 31%; ETA: 0:00:20", IsFinal: false)));
        Assert.Equal(55, reader.Read(new ConsoleLine("Verification 55% complete.", IsFinal: true)));
        Assert.Equal(56, reader.Read(new ConsoleLine("Verification 56% complete.", IsFinal: false, IsOpen: true)));
    }

    [Fact]
    public void Every_carriage_return_update_is_its_own_live_line()
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        assembler.Append("Verification 10% complete.\rVerification 11% complete.\rVerification 12% complete.\r", lines);
        Assert.DoesNotContain(lines, l => l.IsOpen);
        Assert.Equal(
            [new ConsoleLine("Verification 10% complete.", false), new ConsoleLine("Verification 11% complete.", false), new ConsoleLine("Verification 12% complete.", false)],
            lines);
    }

    [Theory]
    [InlineData("Verification 30% complete.", 30.0, "Verification 100% complete.")]
    [InlineData("[=========                  30.0%                          ] ", 30.0, "[==========================100.0%==========================] ")]
    [InlineData("  Progress: 30 of 100 done; Stage: 90%; Total: 30%; ETA:   0:00:10 .", 30.0, "  Progress: 100 of 100 done; Stage: 100%; Total: 100%; ETA:   0:00:00 .")]
    public async Task A_percentage_reaches_the_caller_while_the_tool_still_runs(string first, double expected, string last)
    {
        string gate = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        string script =
            $"[Console]::Out.Write('{first}' + [char]13); [Console]::Out.Flush(); " +
            $"while (-not (Test-Path -LiteralPath '{gate}')) {{ Start-Sleep -Milliseconds 25 }}; " +
            $"[Console]::Out.Write('{last}' + [char]13 + [char]10)";
        var updates = new ConcurrentQueue<RepairUpdate>();
        var progress = new Gate(updates, expected, gate);

        ProcessRun run = await Runner().RunProcessAsync(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            progress,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.True(progress.Opened, "The first percentage arrived only after the tool exited.");
        Assert.Equal(100, run.LastPercent);
        Assert.Equal([expected, 100], updates.Select(u => u.Percent).OfType<double>().Distinct());
        Assert.Contains(updates, u => u is { IsFinal: false, Percent: { } p } && p == expected);
    }

    [Theory]
    [InlineData(RepairOutcome.Succeeded, null, 100.0)]
    [InlineData(RepairOutcome.RestartRequired, null, 100.0)]
    [InlineData(RepairOutcome.IssuesFound, 61.0, 100.0)]
    [InlineData(RepairOutcome.Succeeded, 57.0, 100.0)]
    [InlineData(RepairOutcome.Failed, 62.3, 62.3)]
    [InlineData(RepairOutcome.Failed, null, 0.0)]
    [InlineData(RepairOutcome.Cancelled, 40.0, 40.0)]
    [InlineData(RepairOutcome.NotElevated, null, 0.0)]
    [InlineData(RepairOutcome.StartFailed, null, 0.0)]
    public void A_finished_run_fills_the_bar_and_a_stopped_one_keeps_its_place(RepairOutcome outcome, double? last, double expected)
    {
        var result = new RepairResult(RepairCommandId.FlushDns, outcome, RepairDiagnostic.None, 0, null, last, TimeSpan.FromSeconds(1));
        Assert.Equal(expected, result.FinalPercent);
    }

    private static RepairProcessRunner Runner() => new(new FakeRepairEnvironment(Environment.SystemDirectory, oemCodePage: 437), NullLogger<RepairProcessRunner>.Instance);

    private static List<ConsoleLine> Assemble(string text, int chunk)
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        for (int i = 0; i < text.Length; i += chunk)
        {
            assembler.Append(text.AsSpan(i, Math.Min(chunk, text.Length - i)), lines);
        }

        if (assembler.Complete() is { } last)
        {
            lines.Add(last);
        }

        return lines;
    }

    private static double[] Read(List<ConsoleLine> lines)
    {
        var reader = new ProgressReader();
        return [.. lines.Select(reader.Read).OfType<double>()];
    }

    private static IEnumerable<string> Writes(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static double[] Percents(List<ConsoleLine> lines) =>
        [.. lines.Select(l => RepairProgressParser.TryParse(l.Text)).OfType<double>().Distinct()];

    private sealed class Gate(ConcurrentQueue<RepairUpdate> updates, double expected, string gate) : IProgress<RepairUpdate>
    {
        public bool Opened { get; private set; }

        public void Report(RepairUpdate value)
        {
            updates.Enqueue(value);
            if (!Opened && value.Percent == expected)
            {
                File.WriteAllText(gate, string.Empty);
                Opened = true;
            }
        }
    }
}
