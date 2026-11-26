using Niskala.Interop.Etw;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using static Niskala.Tracker.Tests.Kernel;

namespace Niskala.Tracker.Tests;

public sealed class InstallRecorderTests
{
    private const int Services = 700;
    private const int Root = 1000;

    [Fact]
    public void The_tree_is_followed_and_other_processes_are_not()
    {
        var tracked = new List<int>();
        InstallRecorder recorder = Recorder(installerService: null);
        recorder.AddRoot(Root, @"C:\Downloads\setup.exe", tracked.Add);

        Assert.True(recorder.OnEvent(Start(Root, 50, @"C:\Downloads\setup.exe", parentTracked: false)));
        Assert.True(recorder.OnEvent(Start(1001, Root, @"C:\Users\a\AppData\Local\Temp\is-1.tmp\setup.tmp", parentTracked: true)));
        Assert.False(recorder.OnEvent(Start(2000, 50, @"C:\Windows\explorer.exe", parentTracked: false)));
        Assert.False(recorder.OnEvent(Event(EtwEventKind.FileCreated, NtFile(@"C:\Program Files\Acme\acme.exe"), 3, 1001)));
        Assert.False(recorder.OnEvent(Event(EtwEventKind.FileCreated, @"\Device\Unknown\x", 4, 1001)));
        Assert.False(recorder.OnEvent(Event(EtwEventKind.KeyCreated, @"\REGISTRY\MACHINE\SOFTWARE\Acme", 5, 1001)));
        recorder.OnEvent(Event(EtwEventKind.ProcessStop, string.Empty, 6, 1001));

        RecordedTrace trace = recorder.Snapshot();
        Assert.Equal([Root], tracked);
        Assert.Equal([Root, 1001], trace.Processes.Select(p => p.Id));
        Assert.Equal(50, trace.Processes[0].ParentId);
        Assert.NotNull(trace.Processes[1].ExitedAt);
        Assert.Equal([$@"{Drive}\Program Files\Acme\acme.exe", @"HKLM\SOFTWARE\Acme"], trace.Events.Select(e => e.Path));
        Assert.Equal(1, trace.UnmappedPaths);
        Assert.Equal([Root], recorder.Running().Select(p => p.Id));
    }

    [Fact]
    public void A_running_Windows_Installer_service_is_traced_once_the_tree_runs_msiexec()
    {
        var tracked = new List<int>();
        InstallRecorder recorder = Recorder(installerService: 3000);
        recorder.AddRoot(Root, @"C:\Downloads\setup.exe", tracked.Add);

        recorder.OnEvent(Start(1001, Root, @"C:\Windows\System32\msiexec.exe", parentTracked: true));

        Assert.Equal([Root, 3000], tracked);
        RecordedProcess service = recorder.Snapshot().Processes.Single(p => p.Id == 3000);
        Assert.True(service.Adopted);
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", service.ImagePath);
        Assert.DoesNotContain(recorder.Running(), p => p.Adopted);
    }

    [Fact]
    public void A_Windows_Installer_service_started_later_is_adopted_only_from_services_exe()
    {
        InstallRecorder recorder = Recorder(installerService: null);
        recorder.AddRoot(Root, @"C:\Windows\System32\msiexec.exe", _ => { });

        Assert.False(recorder.OnEvent(Start(3001, 50, NtFile(@"C:\Windows\System32\msiexec.exe"), parentTracked: false)));
        Assert.True(recorder.OnEvent(Start(3002, Services, NtFile(@"C:\Windows\System32\msiexec.exe"), parentTracked: false)));
        Assert.False(recorder.OnEvent(Start(3003, Services, NtFile(@"C:\Windows\System32\svchost.exe"), parentTracked: false)));
        Assert.True(recorder.OnEvent(Start(3004, 3002, NtFile(@"C:\Windows\System32\msiexec.exe"), parentTracked: true)));

        Assert.Equal([Root, 3002, 3004], recorder.Snapshot().Processes.Select(p => p.Id));
        Assert.True(recorder.Snapshot().Processes[1].Adopted);
    }

    private static InstallRecorder Recorder(int? installerService) =>
        new(Translator, new RecorderOptions(Services, () => installerService, _ => @"C:\Windows\System32\msiexec.exe"));

    private static EtwEvent Start(int processId, int parentId, string image, bool parentTracked) =>
        Event(EtwEventKind.ProcessStart, image, 1, processId) with
        {
            ParentProcessId = parentId,
            Flags = parentTracked ? EtwEventFlags.ParentTracked : EtwEventFlags.None,
        };
}
