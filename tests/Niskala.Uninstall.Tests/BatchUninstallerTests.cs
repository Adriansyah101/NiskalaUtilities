using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Batch;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Steam;

namespace Niskala.Uninstall.Tests;

public sealed class BatchUninstallerTests : IDisposable
{
    private const string Uninstaller = @"C:\Program Files\Acme\uninst.exe";
    private const string Root = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private readonly string _scratch = Directory.CreateTempSubdirectory("niskala-batch-").FullName;
    private readonly List<string> _calls = [];
    private readonly List<bool> _restorePoints = [];
    private readonly FakeState _state = new();

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public async Task The_queue_runs_in_order_skips_what_is_blocked_and_asks_for_one_restore_point()
    {
        InstalledApp editor = App("Editor");
        InstalledApp broken = App("Broken") with { UninstallString = null, UninstallCommand = null };
        InstalledApp viewer = App("Viewer");
        BatchUninstaller batch = Batch();

        IReadOnlyList<BatchItem> items = batch.Plan([editor, broken, viewer], UninstallMode.Interactive, TestContext.Current.CancellationToken);
        var progress = new List<BatchProgress>();
        IReadOnlyList<BatchResult> results = await batch.RunAsync(items, restorePoint: true, new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal([BatchBlock.None, BatchBlock.NoCommand, BatchBlock.None], items.Select(i => i.Block));
        Assert.Equal([true, false, false], results.Select(r => r.Removed));
        Assert.Equal(["guardian", "run", "guardian", "run"], _calls);
        Assert.Equal([true, false], _restorePoints.Skip(1));
        Assert.Equal(
            [(0, BatchStep.Started), (0, BatchStep.Finished), (1, BatchStep.Skipped), (2, BatchStep.Started), (2, BatchStep.Failed)],
            progress.Where(p => p.Uninstall is null && p.Clean is null).Select(p => (p.Index, p.Step)));
    }

    [Fact]
    public void One_review_holds_each_leftover_once_and_gives_it_to_the_app_that_scored_it_highest()
    {
        var first = new BatchItem(App("Editor"), null, null, BatchBlock.None);
        var second = new BatchItem(App("Viewer"), null, null, BatchBlock.None);
        LeftoverItem shared = Item(@"C:\Users\u\AppData\Roaming\Acme", 60);
        LeftoverItem sharedBetter = Item(@"C:\Users\u\AppData\Roaming\ACME", 90);
        LeftoverItem editorOnly = Item(@"C:\Program Files\Acme\Editor", 100);
        LeftoverItem viewerOnly = Item(@"C:\Program Files\Acme\Viewer", 85);

        BatchReview review = BatchReview.Merge(
        [
            (first, Report(["not elevated"], shared, editorOnly)),
            (second, Report([], sharedBetter, viewerOnly)),
        ]);

        Assert.Equal([editorOnly, sharedBetter, viewerOnly], review.Items);
        Assert.Same(second, review.OwnerOf(sharedBetter));
        Assert.Null(review.OwnerOf(shared));
        Assert.Equal(["Editor: not elevated"], review.Notes);
        Assert.Equal(1, review.Excluded);
        IReadOnlyList<(BatchItem Owner, IReadOnlyList<LeftoverItem> Items)> groups = review.ByOwner([viewerOnly, editorOnly, sharedBetter, shared]);
        Assert.Equal([second, first], groups.Select(g => g.Owner));
        Assert.Equal([viewerOnly, sharedBetter], groups[0].Items);
        Assert.Equal([editorOnly], groups[1].Items);
    }

    [Theory]
    [InlineData(UninstallOutcome.Removed, true)]
    [InlineData(UninstallOutcome.PendingReboot, true)]
    [InlineData(UninstallOutcome.StaleEntry, true)]
    [InlineData(UninstallOutcome.Unverified, true)]
    [InlineData(UninstallOutcome.StillInstalled, false)]
    [InlineData(UninstallOutcome.Failed, false)]
    [InlineData(UninstallOutcome.Cancelled, false)]
    public void Only_apps_that_are_gone_get_their_leftovers_scanned(UninstallOutcome outcome, bool scanned) =>
        Assert.Equal(scanned, BatchUninstaller.Finished(outcome));

    private InstalledApp App(string name)
    {
        string key = Root + name;
        _state.Keys.Add(key);
        return TestApps.App(InstallerKind.Nsis, $"\"{Uninstaller}\"", environment: new FakeEnvironment(Uninstaller)) with
        {
            Id = "reg:" + name,
            DisplayName = name,
            RegistryKey = key,
            InstallLocation = null,
        };
    }

    private BatchUninstaller Batch()
    {
        var environment = new FakeEnvironment(Uninstaller);
        int runs = 0;
        var runner = new FakeRunner(_calls, onRun: () =>
        {
            if (runs++ == 0)
            {
                _state.Keys.Remove(Root + "Editor");
            }
        });
        var guard = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);
        var store = new TransactionStore(Path.Combine(_scratch, "journal"));
        var collector = new FingerprintCollector(guard, new EmptyTraceSource(), environment, NullLogger<FingerprintCollector>.Instance);
        var fingerprints = new FingerprintStore(Path.Combine(_scratch, "fingerprints"), NullLogger<FingerprintStore>.Instance);
        var cleaner = new LeftoverCleaner(
            guard,
            RegistryGuard.ForCurrentMachine(),
            store,
            new FakeRestorePoints(),
            new FakeFirewallEditor(),
            new FakeTaskEditor(),
            new FakeServices(),
            environment,
            new EmptyParentFinder([], guard),
            NullLogger<LeftoverCleaner>.Instance);
        var trace = new EmptyTraceSource();
        var scanner = new LeftoverScanner(
            guard,
            RegistryGuard.ForCurrentMachine(),
            new NoTrust(),
            new FileLeftoverProbe([], trace),
            new RegistryLeftoverProbe(trace, NullLogger<RegistryLeftoverProbe>.Instance),
            new FirewallLeftoverProbe(new FakeFirewallEditor()),
            new SteamLeftoverProbe(() => null),
            new ScheduledTaskLeftoverProbe(trace),
            new PackageLeftoverProbe(Path.Combine(_scratch, "Packages")),
            environment,
            NullLogger<LeftoverScanner>.Instance);
        UninstallOrchestrator Orchestrator(bool restorePoint)
        {
            _restorePoints.Add(restorePoint);
            return new UninstallOrchestrator(
                new CommandLineResolver(environment, new FakeLocations()),
                new UninstallPreflight(new FakePreflightEnvironment(), TestApps.Guard, NullLogger<UninstallPreflight>.Instance),
                collector,
                fingerprints,
                new RecordingGuardian(_calls),
                runner,
                new FakePackages(_calls),
                _state,
                NullLogger<UninstallOrchestrator>.Instance);
        }

        var steam = new SteamUninstaller(collector, fingerprints, cleaner, new NoSteam(), NullLogger<SteamUninstaller>.Instance);
        return new BatchUninstaller(Orchestrator, steam, scanner, cleaner);
    }

    private static LeftoverItem Item(string path, int score) =>
        new(LeftoverKind.Folder, path, null, score, ConfidenceScorer.TierOf(score), []);

    private static LeftoverReport Report(IReadOnlyList<string> notes, params LeftoverItem[] items) => new()
    {
        AppId = "x",
        ScannedAt = DateTimeOffset.UtcNow,
        Items = items,
        Notes = notes,
        Excluded = notes.Count == 0 ? [] : [new ExcludedLeftover(LeftoverKind.Folder, @"C:\Windows", null, ExclusionReason.Protected, "Windows")],
    };

    private sealed class NoSteam : ISteamClient
    {
        public string? ClientFolder() => null;

        public bool IsRunning(string clientFolder) => false;

        public int Shutdown(string clientFolder) => 0;

        public Task<bool> WaitForExitAsync(string clientFolder, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult(true);

        public int OpenUninstall(string clientFolder, int appId) => 0;
    }

    private sealed class SyncProgress(List<BatchProgress> events) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => events.Add(value);
    }
}
