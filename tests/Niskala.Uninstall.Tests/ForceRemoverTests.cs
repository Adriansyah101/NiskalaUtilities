using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Firewall;
using Niskala.Interop.Processes;
using Niskala.Interop.Services;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Tests;

public sealed class ForceRemoverTests
{
    [Fact]
    public void A_bare_folder_takes_its_parent_as_publisher_unless_the_parent_is_a_known_root()
    {
        InstalledApp nested = ForceRemover.ForFolder(@"C:\Program Files\Acme\Photo Editor", [@"C:\Program Files"]);
        InstalledApp direct = ForceRemover.ForFolder(@"C:\Program Files\Photo Editor", [@"C:\Program Files"]);

        Assert.Equal(("Photo Editor", "Acme"), (nested.DisplayName, nested.Publisher));
        Assert.Null(direct.Publisher);
        Assert.Equal(PathVerdict.Allowed, direct.InstallLocationGuard);
        Assert.Equal(@"folder:C:\Program Files\Photo Editor", direct.Id);
    }

    [Fact]
    public void Selection_takes_safe_items_and_the_apps_own_entry()
    {
        InstalledApp app = TestApps.App(InstallerKind.Nsis) with { RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme" };
        var report = new LeftoverReport
        {
            AppId = app.Id,
            ScannedAt = DateTimeOffset.UtcNow,
            Items =
            [
                new(LeftoverKind.Folder, @"C:\Acme", null, 100, ConfidenceTier.Safe, []),
                new(LeftoverKind.RegistryKey, app.RegistryKey, null, 70, ConfidenceTier.Review, []),
                new(LeftoverKind.Folder, @"C:\Data\Acme", null, 65, ConfidenceTier.Review, []),
                new(LeftoverKind.Folder, @"C:\Data\AcmeCache", null, 10, ConfidenceTier.Low, []),
            ],
        };

        Assert.Equal([@"C:\Acme", app.RegistryKey], ForceRemover.Select(app, report, includeReview: false).Select(i => i.Path));
        Assert.Equal(3, ForceRemover.Select(app, report, includeReview: true).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ends_processes_stops_services_and_removes_the_chosen_leftovers_as_one_transaction(bool chooseNothing)
    {
        string scratch = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string programs = Directory.CreateDirectory(Path.Combine(scratch, "Programs")).FullName;
            string folder = Directory.CreateDirectory(Path.Combine(programs, "NiskalaForce" + Guid.NewGuid().ToString("N")[..8])).FullName;
            string image = Path.Combine(folder, "app.exe");
            File.WriteAllBytes(image, new byte[16]);
            var guard = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);
            var trace = new ServiceTraces(new ServiceTrace("NiskalaForceSvc", null, image, null, 2));
            var processes = new FakeProcesses(new RunningProcess(4242, "app.exe", image), new RunningProcess(77, "other.exe", @"C:\Other\other.exe"));
            var services = new FakeServices { States = { ["NiskalaForceSvc"] = ServiceState.Running } };
            var firewall = new FakeFirewallEditor();
            var store = new TransactionStore(Path.Combine(scratch, "journal"));
            var remover = new ForceRemover(
                new FingerprintCollector(guard, trace, SystemCommandEnvironment.Instance, NullLogger<FingerprintCollector>.Instance),
                new LeftoverScanner(
                    guard,
                    RegistryGuard.ForCurrentMachine(),
                    new NoTrust(),
                    new FileLeftoverProbe([new FileSearchRoot(programs)], trace),
                    new RegistryLeftoverProbe(trace, NullLogger<RegistryLeftoverProbe>.Instance),
                    new FirewallLeftoverProbe(firewall),
                    new SteamLeftoverProbe(() => null),
                    new ScheduledTaskLeftoverProbe(trace),
                    new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
                    SystemCommandEnvironment.Instance,
                    NullLogger<LeftoverScanner>.Instance),
                new LeftoverCleaner(
                    guard,
                    RegistryGuard.ForCurrentMachine(),
                    store,
                    new FakeRestorePoints(),
                    firewall,
                    new FakeTaskEditor(),
                    services,
                    SystemCommandEnvironment.Instance,
                    new EmptyParentFinder([programs], guard),
                    NullLogger<LeftoverCleaner>.Instance),
                processes,
                services,
                NullLogger<ForceRemover>.Instance);
            InstalledApp app = ForceRemover.ForFolder(folder, [programs]);

            ForcePlan plan = remover.Plan(app, inInventory: false, [], TestContext.Current.CancellationToken);
            var options = new ForceOptions { Permanent = true, Chosen = chooseNothing ? [] : null };
            ForceResult result = await remover.ExecuteAsync(plan, [], options, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([4242], plan.Processes.Select(p => p.Id));
            Assert.Equal([4242], processes.Terminated);
            Assert.Equal(["stop NiskalaForceSvc"], services.Calls);
            Assert.Equal(TransactionKind.Force, result.Clean.Transaction.Kind);
            if (chooseNothing)
            {
                Assert.Empty(result.Clean.Transaction.Items);
                Assert.True(Directory.Exists(folder));
                return;
            }

            RemovalRecord removed = Assert.Single(result.Clean.Transaction.Items, i => i.Path == folder);
            Assert.Equal(RemovalStatus.Removed, removed.Status);
            Assert.False(Directory.Exists(folder));
            Assert.Equal(TransactionKind.Force, store.Load(result.Clean.Transaction.Id)?.Kind);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void A_process_is_ended_only_while_its_image_is_the_one_checked()
    {
        string scratch = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
        string copy = Path.Combine(scratch, "ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), copy);
        using Process ping = Process.Start(new ProcessStartInfo(copy, ["-n", "60", "127.0.0.1"]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            RunningProcess listed = Assert.Single(SystemProcessControl.Instance.List(), p => p.Id == ping.Id);
            Assert.Equal(copy, listed.ImagePath, ignoreCase: true);

            TerminateResult refused = SystemProcessControl.Instance.Terminate(listed with { ImagePath = Path.Combine(scratch, "other.exe") }, TimeSpan.FromSeconds(5));
            Assert.Equal(TerminateOutcome.ImageChanged, refused.Outcome);
            Assert.False(ping.HasExited);

            Assert.Equal(TerminateOutcome.Terminated, SystemProcessControl.Instance.Terminate(listed, TimeSpan.FromSeconds(5)).Outcome);
            Assert.True(ping.WaitForExit(5000));
        }
        finally
        {
            if (!ping.HasExited)
            {
                ping.Kill();
                ping.WaitForExit();
            }

            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class ServiceTraces(params ServiceTrace[] services) : ISystemTraceSource
    {
        public IReadOnlyList<ServiceTrace> Services() => services;

        public IReadOnlyList<RegistryValueTrace> RunEntries() => [];

        public IReadOnlyList<RegistryValueTrace> AppPaths() => [];

        public TaskList ScheduledTasks() => new([], 0, 0);

        public IReadOnlyList<ShortcutTrace> Shortcuts() => [];

        public Guid? FindUpgradeCode(Guid productCode, InstallScope scope) => null;

        public bool? IsProductRegistered(Guid productCode) => null;
    }

    private sealed class FakeProcesses(params RunningProcess[] running) : IProcessControl
    {
        public List<int> Terminated { get; } = [];

        public IReadOnlyList<RunningProcess> List() => running;

        public TerminateResult Terminate(RunningProcess process, TimeSpan wait)
        {
            Terminated.Add(process.Id);
            return new TerminateResult(TerminateOutcome.Terminated, 0);
        }
    }
}
