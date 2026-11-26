using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;

namespace Niskala.Uninstall.Tests;

public sealed class JobTrackerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static readonly CompletionOptions Fast = new()
    {
        QuietWindow = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Tracks_the_whole_process_tree_until_the_job_is_empty()
    {
        using JobTracker tracker = Start(@"/c ""ping -n 2 127.0.0.1 >nul""");
        var activity = new FixedActivity();

        UninstallRun run = await Monitor().WaitAsync(tracker, activity, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CompletionReason.AllProcessesExited, run.Reason);
        Assert.Equal(0, run.ExitCode);
        int[] started = [.. run.Events.Where(e => e.Kind == JobEventKind.Started).Select(e => e.ProcessId)];
        Assert.Contains(tracker.RootProcessId, started);
        Assert.Contains(run.Events, e => e.Kind == JobEventKind.Started && e.ImagePath?.EndsWith("PING.EXE", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(run.Events, e => e.Kind == JobEventKind.Empty);
        Assert.Empty(tracker.ActiveProcesses());
    }

    [Fact]
    public async Task Reports_the_root_exit_code()
    {
        using JobTracker tracker = Start("/c exit 7");
        var activity = new FixedActivity();

        UninstallRun run = await Monitor().WaitAsync(tracker, activity, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, run.ExitCode);
    }

    [Fact]
    public async Task Disposing_the_tracker_leaves_the_processes_running()
    {
        int processId;
        using (JobTracker tracker = Start(@"/c ""ping -n 3 127.0.0.1 >nul"""))
        {
            processId = tracker.RootProcessId;
        }

        using Process process = Process.GetProcessById(processId);
        Assert.False(process.HasExited);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Missing_executable_returns_the_win32_error()
    {
        var command = new ResolvedCommand(Path.Combine(Environment.SystemDirectory, "niskala-missing.exe"), string.Empty, CommandOrigin.UninstallString, false);

        int error = JobTracker.TryStart(command, NullLogger<JobTracker>.Instance, out JobTracker? tracker);

        Assert.Equal(2, error);
        Assert.Null(tracker);
    }

    [Fact]
    public void Folder_changes_move_the_activity_stamp()
    {
        string root = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var probe = new UninstallActivityProbe(null, root, NullLogger<UninstallActivityProbe>.Instance);
            long before = probe.Stamp();
            File.WriteAllText(Path.Combine(root, "leftover.txt"), "x");

            Assert.True(SpinWait.SpinUntil(() => probe.Stamp() != before, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JobTracker Start(string arguments)
    {
        var command = new ResolvedCommand(Cmd, arguments, CommandOrigin.UninstallString, false);
        Assert.Equal(0, JobTracker.TryStart(command, NullLogger<JobTracker>.Instance, out JobTracker? tracker));
        return tracker!;
    }

    private static CompletionMonitor Monitor() => new(Fast, () => false, NullLogger<CompletionMonitor>.Instance);

    private sealed class FixedActivity : IActivityProbe
    {
        public long Stamp() => 0;
    }
}
