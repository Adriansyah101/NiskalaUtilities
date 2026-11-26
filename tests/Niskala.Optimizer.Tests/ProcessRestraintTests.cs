using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Interop.Processes;
using Niskala.Optimizer.Processes;

namespace Niskala.Optimizer.Tests;

public sealed class ProcessRestraintTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly FakeProcesses _processes = new();

    [Theory]
    [InlineData(0, ProcessRefusal.SystemProcess)]
    [InlineData(4, ProcessRefusal.SystemProcess)]
    [InlineData(500, ProcessRefusal.Niskala)]
    [InlineData(400, ProcessRefusal.NiskalaAncestor)]
    [InlineData(300, ProcessRefusal.NiskalaAncestor)]
    [InlineData(999, ProcessRefusal.Exited)]
    public void The_guard_never_lets_system_or_own_processes_through(int processId, ProcessRefusal expected) =>
        Assert.Equal(expected, new ProcessGuard(_processes).Check(processId));

    [Fact]
    public void Other_sessions_windows_programs_niskala_files_and_critical_processes_are_refused()
    {
        _processes.Add(600, @"C:\Apps\service.exe", session: 0);
        _processes.Add(601, @"C:\Apps\other-user.exe", session: 2);
        _processes.Add(602, @"C:\Windows\System32\svchost.exe");
        _processes.Add(603, @"C:\Elsewhere\NiskalaHelper.exe");
        _processes.Add(604, @"C:\Tools\Niskala\lib\worker.exe");
        _processes.Add(605, @"C:\Apps\critical.exe");
        _processes.Critical[605] = true;
        _processes.Add(606, @"C:\Apps\protected.exe");
        _processes.Critical[606] = null;
        _processes.Add(607, @"C:\Apps\idle-sync.exe");
        _processes.Processes.Add(new ProcessEntry(608, "ghost.exe", 300));
        _processes.Sessions[608] = 1;
        var guard = new ProcessGuard(_processes);

        Assert.Equal(
            [ProcessRefusal.OtherSession, ProcessRefusal.OtherSession, ProcessRefusal.WindowsComponent, ProcessRefusal.Niskala, ProcessRefusal.Niskala,
                ProcessRefusal.Critical, ProcessRefusal.CriticalUnknown, ProcessRefusal.None, ProcessRefusal.ImageUnknown],
            Enumerable.Range(600, 9).Select(id => guard.Check(id)));
    }

    [Fact]
    public void Suspension_is_remembered_and_undone_only_for_the_same_process()
    {
        _processes.Add(700, @"C:\Apps\sync.exe");
        _processes.Add(701, @"C:\Apps\indexer.exe");
        ProcessRestrainer restrainer = Restrainer();

        Assert.Equal(RestraintOutcome.Applied, restrainer.Suspend(700).Outcome);
        Assert.Equal(RestraintOutcome.Applied, restrainer.Suspend(701).Outcome);
        Assert.Equal([700, 701], _processes.Suspended.Order());
        _processes.Started[701] = _processes.Started[701].AddMinutes(5);

        Assert.Equal([700], restrainer.Current().Select(r => r.ProcessId));
        Assert.Equal(RestraintOutcome.NotRestrained, restrainer.Undo(701).Outcome);
        Assert.Equal(RestraintOutcome.Undone, restrainer.Undo(700).Outcome);
        Assert.Equal([701], _processes.Suspended);
        Assert.Empty(restrainer.Current());
    }

    [Fact]
    public void Efficiency_mode_lowers_priority_and_puts_the_old_one_back()
    {
        _processes.Add(710, @"C:\Apps\render.exe");
        _processes.Priorities[710] = PriorityClass.AboveNormal;
        ProcessRestrainer restrainer = Restrainer();

        restrainer.SetEfficiency(710);
        Assert.Equal(PriorityClass.Idle, _processes.Priorities[710]);
        Assert.Contains(710, _processes.Efficient);

        restrainer.Undo(710);
        Assert.Equal(PriorityClass.AboveNormal, _processes.Priorities[710]);
        Assert.DoesNotContain(710, _processes.Efficient);
    }

    [Fact]
    public void A_refused_process_is_left_untouched()
    {
        _processes.Add(720, @"C:\Windows\System32\dwm.exe");

        RestraintResult result = Restrainer().Suspend(720);

        Assert.Equal((RestraintOutcome.Refused, ProcessRefusal.WindowsComponent), (result.Outcome, result.Refusal));
        Assert.Empty(_processes.Suspended);
    }

    [Fact]
    public async Task Candidates_flag_idle_background_processes()
    {
        _processes.Add(730, @"C:\Apps\tray.exe");
        _processes.Add(731, @"C:\Apps\editor.exe");
        _processes.Windowed.Add(731);

        IReadOnlyList<ProcessCandidate> candidates = await Restrainer().ListAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.True(candidates.Single(c => c.Id == 730) is { CanRestrain: true, IsIdleBackground: true });
        Assert.True(candidates.Single(c => c.Id == 731) is { CanRestrain: true, IsIdleBackground: false });
        Assert.False(candidates.Single(c => c.Id == 300).CanRestrain);
    }

    [Fact]
    public void A_real_process_can_be_suspended_eco_throttled_capped_and_released()
    {
        string ping = Path.Combine(_sandbox.Folder, "PING.EXE");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping);
        using Process process = Process.Start(new ProcessStartInfo(ping, ["-n", "60", "127.0.0.1"]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var restrainer = new ProcessRestrainer(SystemProcesses.Instance, new ProcessGuard(SystemProcesses.Instance), new RestraintStore(Path.Combine(_sandbox.Folder, "r.json")), NullLogger<ProcessRestrainer>.Instance);

            Assert.Equal(RestraintOutcome.Applied, restrainer.Suspend(process.Id).Outcome);
            Assert.Equal(RestraintOutcome.Applied, restrainer.SetEfficiency(process.Id).Outcome);
            Assert.Equal(ProcessRestraintNative.ErrorSuccess, ProcessRestraintNative.TryGetPriority(process.Id, out PriorityClass lowered));
            Assert.Equal(PriorityClass.Idle, lowered);
            Assert.Equal(RestraintOutcome.Applied, restrainer.Cap(process.Id, 10, out CpuCapJob? cap).Outcome);
            using (cap)
            {
                Assert.Equal(0, cap!.Change(25));
            }

            Assert.Equal(RestraintOutcome.Undone, restrainer.Undo(process.Id).Outcome);
            Assert.Equal(ProcessRestraintNative.ErrorSuccess, ProcessRestraintNative.TryGetPriority(process.Id, out PriorityClass restored));
            Assert.Equal(PriorityClass.Normal, restored);
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    public void Dispose() => _sandbox.Dispose();

    private ProcessRestrainer Restrainer() =>
        new(_processes, new ProcessGuard(_processes), new RestraintStore(Path.Combine(_sandbox.Folder, "restraints.json")), NullLogger<ProcessRestrainer>.Instance);
}
