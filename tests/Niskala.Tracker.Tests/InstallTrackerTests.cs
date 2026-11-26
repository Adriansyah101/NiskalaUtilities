using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Interop.Etw;
using Niskala.Interop.Registry;
using Niskala.Scanner.Leftovers;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Launching;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Rollback;
using Niskala.Tracker.Tracing;
using static Niskala.Tracker.Tests.Kernel;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Tracker.Tests;

public sealed class InstallTrackerTests
{
    private const int Root = 1000;
    private const int RegSz = 1;

    [Fact]
    public async Task A_recorded_install_is_saved_as_a_log_and_rolled_back_exactly()
    {
        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        string shared = scratch.Dir("Shared");
        string app = Path.Combine(scratch.Root, "Acme");
        using (RegistryKey key = registry.Create("Shared"))
        {
            key.SetValue("Path", "before");
            key.SetValue("Later", "before");
        }

        var trace = new FakeTrace();
        var launcher = new FakeLauncher(trace, emit =>
        {
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "acme.exe"), "exe");
            File.WriteAllText(Path.Combine(shared, "plugin.dll"), "dll");
            File.WriteAllText(Path.Combine(app, "setup.tmp"), "tmp");
            File.Delete(Path.Combine(app, "setup.tmp"));
            using (RegistryKey settings = registry.Create(@"Acme\Settings"))
            {
                settings.SetValue("Theme", "dark");
            }

            using (RegistryKey key = registry.Create("Shared"))
            {
                key.SetValue("Path", "after");
                key.SetValue("Later", "after");
                key.SetValue("Added", "new");
            }

            emit(Event(EtwEventKind.ProcessStart, NtFile(Path.Combine(app, "helper.exe")), 1, 1001) with { ParentProcessId = Root, Flags = EtwEventFlags.ParentTracked });
            emit(Event(EtwEventKind.FileCreated, NtFile(app), 2) with { Flags = EtwEventFlags.Directory });
            emit(Event(EtwEventKind.FileCreated, NtFile(Path.Combine(app, "acme.exe")), 3));
            emit(Event(EtwEventKind.FileCreated, NtFile(Path.Combine(shared, "plugin.dll")), 4, 1001));
            emit(Event(EtwEventKind.FileCreated, NtFile(Path.Combine(app, "setup.tmp")), 5));
            emit(Event(EtwEventKind.FileDeleted, NtFile(Path.Combine(app, "setup.tmp")), 6));
            emit(Event(EtwEventKind.KeyCreated, registry.Kernel("Acme"), 7) with { Flags = EtwEventFlags.NewKey });
            emit(Event(EtwEventKind.KeyCreated, registry.Kernel(@"Acme\Settings"), 8) with { Flags = EtwEventFlags.NewKey });
            emit(Event(EtwEventKind.ValueSet, registry.Kernel(@"Acme\Settings"), 9) with { Name = "Theme" });
            emit(ValueSet(registry.Kernel("Shared"), "Path", 10, "before"));
            emit(ValueSet(registry.Kernel("Shared"), "Later", 11, "before"));
            emit(Event(EtwEventKind.ValueSet, registry.Kernel("Shared"), 12) with { Name = "Added", Flags = EtwEventFlags.Previous });
        });

        var store = new InstallLogStore(Path.Combine(scratch.Root, "logs"));
        InstallLog log = await Tracker(trace, launcher, LiveSystemState.Instance, store)
            .RunAsync(new TrackRequest(@"C:\Downloads\acme-setup.exe", "/S", "Acme"), null, CancellationToken.None, TestContext.Current.CancellationToken);

        Assert.Equal([Root], trace.Tracked);
        Assert.Equal([Root, 1001], log.Processes.Select(p => p.Id));
        Assert.Equal(0, log.ExitCode);
        Assert.Equal([app, Path.Combine(app, "acme.exe"), Path.Combine(shared, "plugin.dll")], log.Files.Select(f => f.Path));
        Assert.Equal([registry.Path("Acme"), registry.Path(@"Acme\Settings")], log.Keys.Select(k => k.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["Added", "Later", "Path"], log.Values.Select(v => v.Name));
        Assert.Equal(Text("before"), log.Values.Single(v => v.Name == "Path").PreviousData);
        Assert.Equal(log.Id, store.Load(log.Id)?.Id);

        using (RegistryKey key = registry.Create("Shared"))
        {
            key.SetValue("Later", "changed by the user");
        }

        var journal = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        var rollback = new LogRollback(Guards.Cleaner(journal, Guards.Real()), store, LiveSystemState.Instance, NullLogger<LogRollback>.Instance);
        RollbackPlan plan = rollback.Plan(log);

        Assert.Equal(
            [(LeftoverKind.Folder, app), (LeftoverKind.File, Path.Combine(shared, "plugin.dll")), (LeftoverKind.RegistryKey, registry.Path("Acme")), (LeftoverKind.RegistryValue, registry.Path("Shared"))],
            plan.Items.Select(i => (i.Kind, i.Path)));
        Assert.Equal(["Later", "Path"], plan.Reverts.Select(r => r.Previous.Name));
        Assert.Contains("Events were lost", Assert.Single(plan.Warnings), StringComparison.Ordinal);

        CleanResult result = await rollback.ExecuteAsync(plan, new CleanOptions { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionKind.Rollback, result.Transaction.Kind);
        Assert.False(Directory.Exists(app));
        Assert.False(File.Exists(Path.Combine(shared, "plugin.dll")));
        Assert.Null(Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme"));
        using (RegistryKey key = registry.Create("Shared"))
        {
            Assert.Equal("before", key.GetValue("Path"));
            Assert.Equal("changed by the user", key.GetValue("Later"));
            Assert.Null(key.GetValue("Added"));
        }

        Assert.Equal(RemovalStatus.Reverted, result.Transaction.Items.Single(i => i.ValueName == "Path").Status);
        Assert.Equal("changed since the install", result.Transaction.Items.Single(i => i.ValueName == "Later").Detail);
        Assert.Equal(result.Transaction.Id, store.Load(log.Id)?.RollbackTransactionId);
        Assert.Contains(rollback.Plan(store.Load(log.Id)!).Warnings, w => w.Contains("already rolled back", StringComparison.Ordinal));

        RestoreReport report = new TransactionRestorer(journal, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(result.Transaction.Id)!;
        using (RegistryKey? settings = Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme\Settings"))
        {
            Assert.Equal("dark", settings?.GetValue("Theme"));
        }

        Assert.Contains(report.Notes, n => n.Contains("written back to their earlier data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Uninstall_entry_names_the_app_and_noise_is_counted()
    {
        const string Entry = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Acme_is1";
        var trace = new FakeTrace();
        var state = new FakeState();
        state.Keys.Add(Entry);
        state.Keys.Add(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\X");
        state.Files[Path.Combine(Environment.SystemDirectory, "acme.sys")] = FileState.File;
        var launcher = new FakeLauncher(trace, emit =>
        {
            emit(Event(EtwEventKind.KeyCreated, $@"\REGISTRY\USER\{Sid.Current}\Software\Microsoft\Windows\CurrentVersion\Uninstall\Acme_is1", 1) with { Flags = EtwEventFlags.NewKey });
            emit(Event(EtwEventKind.KeyCreated, $@"\REGISTRY\USER\{Sid.Current}\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\X", 2) with { Flags = EtwEventFlags.NewKey });
            emit(Event(EtwEventKind.FileCreated, NtFile(Path.Combine(Environment.SystemDirectory, "acme.sys")), 3));
        });

        using var scratch = new ScratchFolder();
        InstallLog log = await Tracker(trace, launcher, state, new InstallLogStore(scratch.Root))
            .RunAsync(new TrackRequest(@"C:\Downloads\acme.exe", null, "Acme"), null, CancellationToken.None, TestContext.Current.CancellationToken);

        Assert.Equal(Entry, log.UninstallKey);
        Assert.Equal("reg:hkcu:Acme_is1", log.AppId);
        Assert.Equal(1, log.Filtered["Shell state"]);
        Assert.Contains(log.Filtered.Keys, k => k.StartsWith("Protected", StringComparison.Ordinal));
        Assert.Contains(log.Notes, n => n.Contains("events came from processes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finishing_early_saves_the_log_and_names_what_still_runs()
    {
        var trace = new FakeTrace();
        var launcher = new FakeLauncher(trace, _ => { }) { Wait = Timeout.InfiniteTimeSpan };
        using var finish = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var scratch = new ScratchFolder();

        InstallLog log = await Tracker(trace, launcher, new FakeState(), new InstallLogStore(scratch.Root))
            .RunAsync(new TrackRequest(@"C:\Downloads\acme.exe", null, "Acme"), null, finish.Token, TestContext.Current.CancellationToken);

        Assert.Null(log.ExitCode);
        Assert.Contains(log.Notes, n => n.Contains("still running: acme.exe", StringComparison.Ordinal));
    }

    private static InstallTracker Tracker(FakeTrace trace, FakeLauncher launcher, ISystemState state, InstallLogStore store) => new(
        trace,
        launcher,
        state,
        new FakeSystemServices(),
        new NoiseFilter([], DefaultNoise.Keys, Guards.Real(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), RegistryGuard.ForCurrentMachine()),
        store,
        Translator,
        NullLogger<InstallTracker>.Instance,
        TimeSpan.Zero);

    private static EtwEvent ValueSet(string key, string name, int second, string previous) =>
        Event(EtwEventKind.ValueSet, key, second) with
        {
            Name = name,
            Flags = EtwEventFlags.Previous,
            PreviousType = RegSz,
            PreviousSize = Text(previous).Length,
            PreviousData = Text(previous),
        };

    private static byte[] Text(string text) => Encoding.Unicode.GetBytes(text + "\0");

    private sealed class FakeTrace : ITraceSource
    {
        public List<int> Tracked { get; } = [];

        public ITraceSession Start(Func<EtwEvent, bool> onEvent)
        {
            Handler = onEvent;
            return new Session(this);
        }

        public Func<EtwEvent, bool>? Handler { get; private set; }

        private sealed class Session(FakeTrace trace) : ITraceSession
        {
            public void Track(int processId) => trace.Tracked.Add(processId);

            public EtwStats Stop() => new(100, 12, 0, 0, 1, 0);

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeLauncher(FakeTrace trace, Action<Action<EtwEvent>> install) : IInstallerLauncher
    {
        public TimeSpan Wait { get; init; } = TimeSpan.Zero;

        private FakeTrace Trace { get; } = trace;

        private Action<Action<EtwEvent>> Install { get; } = install;

        public ILaunchedInstaller Start(string installer, string? arguments) => new Launched(this, installer);

        private sealed class Launched(FakeLauncher launcher, string installer) : ILaunchedInstaller
        {
            private bool _done;

            public int ProcessId => Root;

            public string ImagePath => installer;

            public int? ExitCode => _done ? 0 : null;

            public void Resume() => launcher.Install(e => launcher.Trace.Handler!(e));

            public async Task WaitForTreeAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(launcher.Wait, cancellationToken).ConfigureAwait(false);
                _done = true;
            }

            public void Dispose()
            {
            }
        }
    }
    private sealed class FakeSystemServices : ISystemServices
    {
        public ServiceSnapshot Capture() => new(new HashSet<string>(), []);

        public string? ServiceImage(string name) => null;

        public int? ServicesProcessId() => 700;

        public int? InstallerServiceProcessId() => null;

        public string? ImagePath(int processId) => null;
    }
}
