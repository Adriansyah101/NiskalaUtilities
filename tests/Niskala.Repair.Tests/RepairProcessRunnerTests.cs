using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Repair;
using Niskala.Repair.Output;

namespace Niskala.Repair.Tests;

public sealed class RepairProcessRunnerTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly string _folder = Directory.CreateTempSubdirectory("niskala-repair-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Without_elevation_no_command_starts()
    {
        var runner = Runner(new FakeRepairEnvironment(_folder, elevated: false));
        var updates = new Collector();
        foreach (RepairCommand command in RepairCommandCatalog.All)
        {
            RepairResult result = await runner.RunAsync(command.Id, updates, TestContext.Current.CancellationToken);
            Assert.Equal(RepairOutcome.NotElevated, result.Outcome);
            Assert.Equal(RepairDiagnostic.ElevationRequired, result.Diagnostic);
            Assert.Null(result.ExitCode);
            Assert.False(result.Succeeded);
        }

        Assert.Empty(updates.All);
    }

    [Fact]
    public async Task A_missing_program_is_reported_without_a_start()
    {
        RepairResult result = await Runner(new FakeRepairEnvironment(_folder)).RunAsync(RepairCommandId.FlushDns, null, TestContext.Current.CancellationToken);
        Assert.Equal(RepairOutcome.StartFailed, result.Outcome);
        Assert.Equal(RepairDiagnostic.ProgramMissing, result.Diagnostic);
        Assert.Equal(RepairExitCodes.FileNotFound, result.StartError);
    }

    [Fact]
    public async Task A_cancelled_token_starts_nothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        RepairResult result = await Runner(new FakeRepairEnvironment(Environment.SystemDirectory)).RunAsync(RepairCommandId.FlushDns, null, cancel.Token);
        Assert.Equal(RepairOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Utf16_progress_from_a_real_pipe_is_decoded_and_parsed()
    {
        string replay = Path.Combine(_folder, "sfc.txt");
        await File.WriteAllBytesAsync(replay, Encoding.Unicode.GetBytes(
            "Beginning verification phase of system scan.\r\nVerification 12% complete.\rVerification 64% complete.\rVerification 100% complete.\r\r\n"),
            TestContext.Current.CancellationToken);

        var updates = new Collector();
        ProcessRun run = await Runner(new FakeRepairEnvironment(Environment.SystemDirectory, oemCodePage: 437))
            .RunProcessAsync(Cmd, ["/d", "/c", "type", replay], updates, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(100, run.LastPercent);
        Assert.Equal(["Beginning verification phase of system scan.", "Verification 100% complete."], updates.Final(RepairStream.Output));
        Assert.Equal([12.0, 64, 100], updates.All.Select(u => u.Percent).OfType<double>().Distinct());
    }

    [Fact]
    public async Task Oem_text_from_a_real_pipe_uses_the_oem_code_page()
    {
        string replay = Path.Combine(_folder, "dism.txt");
        await File.WriteAllBytesAsync(replay, ConsoleTextDecoder.OemEncoding(850).GetBytes("Überprüfung abgeschlossen.\r\n[=====  25,0%  ]\r\n"), TestContext.Current.CancellationToken);

        var updates = new Collector();
        ProcessRun run = await Runner(new FakeRepairEnvironment(Environment.SystemDirectory, oemCodePage: 850))
            .RunProcessAsync(Cmd, ["/d", "/c", "type", replay], updates, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(["Überprüfung abgeschlossen.", "[=====  25,0%  ]"], updates.Final(RepairStream.Output));
        Assert.Equal(25, run.LastPercent);
    }

    [Fact]
    public async Task Standard_error_is_read_apart_from_standard_output()
    {
        var updates = new Collector();
        ProcessRun run = await Runner(SystemRepairEnvironment.Instance)
            .RunProcessAsync(Cmd, ["/d", "/c", "type", Path.Combine(_folder, "absent.txt")], updates, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, run.ExitCode);
        Assert.NotEmpty(updates.Final(RepairStream.Error));
        Assert.Empty(updates.Final(RepairStream.Output));
    }

    [Fact]
    public async Task Cancelling_ends_the_whole_process_tree()
    {
        int[] before = PingIds();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new Collector(_ => started.TrySetResult());

        long start = Stopwatch.GetTimestamp();
        Task<ProcessRun> running = Runner(SystemRepairEnvironment.Instance)
            .RunProcessAsync(Cmd, ["/d", "/c", "ping.exe", "-n", "60", "127.0.0.1"], updates, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        int[] children = [.. PingIds().Except(before)];
        Assert.NotEmpty(children);
        await cancel.CancelAsync();
        ProcessRun run = await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(run.Cancelled);
        Assert.Null(run.ExitCode);
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(30));
        foreach (int id in children)
        {
            Assert.True(HasExited(id), $"ping {id} still runs");
        }
    }

    [Fact]
    public async Task The_dns_flush_runs_end_to_end_when_elevated()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Flushing the DNS cache needs an elevated test run.");
        }

        var updates = new Collector();
        RepairResult result = await Runner(SystemRepairEnvironment.Instance).RunAsync(RepairCommandId.FlushDns, updates, TestContext.Current.CancellationToken);

        Assert.Equal(RepairOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(updates.Final(RepairStream.Output), line => line.Length > 0);
        Assert.DoesNotContain(updates.All, u => u.Text.Contains('�', StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_component_store_check_runs_end_to_end_when_elevated()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Skip("The component store check needs an elevated test run.");
        }

        var updates = new Collector();
        RepairResult result = await Runner(SystemRepairEnvironment.Instance).RunAsync(RepairCommandId.DismCheckHealth, updates, TestContext.Current.CancellationToken);

        Assert.Equal(RepairOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(updates.Final(RepairStream.Output));
        Assert.DoesNotContain(updates.All, u => u.Text.Contains('�', StringComparison.Ordinal));
    }

    private static RepairProcessRunner Runner(IRepairEnvironment environment) => new(environment, NullLogger<RepairProcessRunner>.Instance);

    private static int[] PingIds()
    {
        Process[] processes = Process.GetProcessesByName("PING");
        try
        {
            return [.. processes.Select(p => p.Id)];
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool HasExited(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class Collector(Action<RepairUpdate>? onUpdate = null) : IProgress<RepairUpdate>
    {
        private readonly ConcurrentQueue<RepairUpdate> _updates = new();

        public RepairUpdate[] All => [.. _updates];

        public string[] Final(RepairStream stream) => [.. _updates.Where(u => u.IsFinal && u.Stream == stream && u.Text.Length > 0).Select(u => u.Text)];

        public void Report(RepairUpdate value)
        {
            _updates.Enqueue(value);
            onUpdate?.Invoke(value);
        }
    }
}
