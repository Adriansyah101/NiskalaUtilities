using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Tests;

public sealed class CompletionDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly RunningProcess Uninstaller = new(10, "uninst.exe", @"C:\Program Files\Acme\uninst.exe");
    private static readonly RunningProcess Browser = new(20, "msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");

    private readonly CompletionDetector _detector = new([@"C:\Program Files\Acme"]);

    [Fact]
    public void Completes_after_the_job_empties_and_three_quiet_seconds_pass()
    {
        Assert.Equal(CompletionPhase.Running, Observe(0, [Uninstaller]));
        Assert.Equal(CompletionPhase.Settling, Observe(1, []));
        Assert.Equal(CompletionPhase.Settling, Observe(3.9, []));
        Assert.Equal(CompletionPhase.Completed, Observe(4, []));
        Assert.Equal(CompletionReason.AllProcessesExited, _detector.Reason);
        Assert.Empty(_detector.Tail);
    }

    [Fact]
    public void Activity_on_the_key_or_folder_restarts_the_quiet_window()
    {
        Observe(0, [], stamp: 1);
        Assert.Equal(CompletionPhase.Settling, Observe(2, [], stamp: 2));
        Assert.Equal(CompletionPhase.Settling, Observe(4.5, [], stamp: 2));
        Assert.Equal(CompletionPhase.Completed, Observe(5, [], stamp: 2));
    }

    [Fact]
    public void A_new_process_in_the_job_sends_it_back_to_running()
    {
        Observe(0, []);
        Assert.Equal(CompletionPhase.Running, Observe(2, [Uninstaller with { Id = 11, Name = "Au_.exe" }]));
        Assert.Equal(CompletionPhase.Settling, Observe(3, []));
        Assert.Equal(CompletionPhase.Settling, Observe(5.5, []));
        Assert.Equal(CompletionPhase.Completed, Observe(6, []));
    }

    [Fact]
    public void Windows_installer_transaction_outside_the_job_is_waited_for()
    {
        Assert.Equal(CompletionPhase.WaitingForInstaller, Observe(0, [], busy: true));
        Assert.Equal(CompletionPhase.WaitingForInstaller, Observe(10, [], busy: true));
        Assert.Equal(CompletionPhase.Settling, Observe(11, []));
        Assert.Equal(CompletionPhase.Completed, Observe(14, []));
    }

    [Fact]
    public void A_survey_browser_left_alone_is_a_benign_tail()
    {
        Assert.Equal(CompletionPhase.Running, Observe(0, [Uninstaller, Browser]));
        Assert.Equal(CompletionPhase.Settling, Observe(1, [Browser]));
        Assert.Equal(CompletionPhase.Completed, Observe(4, [Browser, Browser with { Id = 21 }]));
        Assert.Equal(CompletionReason.BenignTailOnly, _detector.Reason);
        Assert.Equal(2, _detector.Tail.Count);
    }

    [Fact]
    public void A_browser_from_the_install_folder_or_without_a_path_is_not_benign()
    {
        var bundled = new RunningProcess(30, "chrome.exe", @"C:\Program Files\Acme\chrome.exe");
        var unknown = new RunningProcess(31, "chrome.exe", null);

        Assert.Equal(CompletionPhase.Running, Observe(0, [bundled]));
        Assert.Equal(CompletionPhase.Running, Observe(10, [unknown]));
    }

    [Fact]
    public void Endless_activity_completes_at_the_settle_limit()
    {
        long stamp = 0;
        for (double second = 0; second < 60; second += 1)
        {
            Assert.Equal(CompletionPhase.Settling, Observe(second, [], stamp: ++stamp));
        }

        Assert.Equal(CompletionPhase.Completed, Observe(60, [], stamp: ++stamp));
        Assert.Equal(CompletionReason.SettleTimeout, _detector.Reason);
    }

    private CompletionPhase Observe(double seconds, RunningProcess[] processes, bool busy = false, long stamp = 0) =>
        _detector.Observe(new CompletionSample(T0.AddSeconds(seconds), processes, busy, stamp));
}
