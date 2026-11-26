using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Guardian;
using Niskala.Scanner.Fingerprints;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Packages;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Verification;

namespace Niskala.Uninstall.Tests;

public sealed class UninstallOrchestratorTests : IDisposable
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme";

    private readonly string _store = Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private readonly FakeState _state = new();

    public void Dispose()
    {
        if (Directory.Exists(_store))
        {
            Directory.Delete(_store, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_the_steps_in_order_and_verifies_the_entry_is_gone()
    {
        var runner = new FakeRunner(_calls, exitCode: 0, onRun: () => _state.Keys.Remove(Key));
        UninstallOrchestrator orchestrator = Orchestrator(runner);
        UninstallPlan plan = orchestrator.Plan(NsisApp(), UninstallMode.Silent, TestContext.Current.CancellationToken);

        UninstallResult result = await orchestrator.ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(UninstallOutcome.Removed, result.Outcome);
        Assert.Equal(["guardian", "run"], _calls);
        Assert.Equal(@"/S", runner.Command!.Arguments);
        Assert.Equal(Key, runner.RegistryKey);
        Assert.False(result.Verification!.KeyPresent);
        Assert.False(result.Checkpoint!.CanRollBack);
        Assert.True(File.Exists(result.FingerprintFile));
        Assert.Contains(result.Notes, n => n.Contains("NSIS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallerKind.Nsis, 1, UninstallOutcome.Cancelled)]
    [InlineData(InstallerKind.Nsis, 2, UninstallOutcome.Failed)]
    [InlineData(InstallerKind.Msi, 3010, UninstallOutcome.PendingReboot)]
    [InlineData(InstallerKind.Msi, 1605, UninstallOutcome.StaleEntry)]
    [InlineData(InstallerKind.Msi, 1602, UninstallOutcome.Cancelled)]
    [InlineData(InstallerKind.InnoSetup, 0, UninstallOutcome.StillInstalled)]
    public async Task Maps_the_exit_code_when_the_entry_stays(InstallerKind kind, int exitCode, UninstallOutcome expected)
    {
        InstalledApp app = NsisApp() with { InstallerKind = kind };
        UninstallOrchestrator orchestrator = Orchestrator(new FakeRunner(_calls, exitCode));

        UninstallResult result = await orchestrator.ExecuteAsync(orchestrator.Plan(app, UninstallMode.Interactive, TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.True(result.Verification!.StillRegistered);
    }

    [Fact]
    public async Task Reports_an_uninstaller_that_demands_elevation()
    {
        UninstallOrchestrator orchestrator = Orchestrator(new FakeRunner(_calls, startError: 740));

        UninstallResult result = await orchestrator.ExecuteAsync(orchestrator.Plan(NsisApp(), UninstallMode.Interactive, TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(UninstallOutcome.NotStarted, result.Outcome);
        Assert.Equal(740, result.StartError);
        Assert.Null(result.Verification);
        Assert.Contains(result.Notes, n => n.Contains("elevation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removes_a_package_through_package_manager()
    {
        const string FullName = "Acme.App_1.0.0.0_x64__abc";
        InstalledApp app = new()
        {
            Id = "appx:Acme.App_abc",
            Sources = AppSources.Appx,
            Scope = InstallScope.User,
            DisplayName = "Acme App",
            InstallerKind = InstallerKind.Msix,
            Package = new PackageDetails(FullName, "Acme.App_abc", PackageSignatureKind.Store, null, null, false),
        };
        _state.Packages.Add(FullName);
        var packages = new FakePackages(_calls, () => _state.Packages.Remove(FullName));
        var runner = new FakeRunner(_calls);
        UninstallOrchestrator orchestrator = Orchestrator(runner, packages);

        UninstallResult result = await orchestrator.ExecuteAsync(orchestrator.Plan(app, UninstallMode.Interactive, TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(UninstallOutcome.Removed, result.Outcome);
        Assert.Equal(["guardian", "package " + FullName], _calls);
        Assert.False(result.Verification!.PackagePresent);
        Assert.True(result.Package!.Succeeded);
    }

    [Fact]
    public async Task A_protected_package_is_never_removed()
    {
        InstalledApp app = new()
        {
            Id = "appx:Windows.Shell",
            Sources = AppSources.Appx,
            Scope = InstallScope.User,
            InstallerKind = InstallerKind.Msix,
            IsProtected = true,
            Package = new PackageDetails("Windows.Shell_1_x64__cw5n1h2txyewy", "Windows.Shell_cw5n1h2txyewy", PackageSignatureKind.System, null, null, false),
        };
        UninstallOrchestrator orchestrator = Orchestrator(new FakeRunner(_calls), new FakePackages(_calls));

        UninstallPlan plan = orchestrator.Plan(app, UninstallMode.Interactive, TestContext.Current.CancellationToken);
        UninstallResult result = await orchestrator.ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(plan.Preflight.CanProceed);
        Assert.Equal(UninstallOutcome.Blocked, result.Outcome);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task The_preflight_runs_again_right_before_starting()
    {
        var environment = new FakePreflightEnvironment();
        UninstallOrchestrator orchestrator = Orchestrator(new FakeRunner(_calls), preflight: environment);
        UninstallPlan plan = orchestrator.Plan(NsisApp() with { InstallerKind = InstallerKind.WixBurn }, UninstallMode.Interactive, TestContext.Current.CancellationToken);
        Assert.True(plan.Preflight.CanProceed);

        environment.Busy = true;
        UninstallResult result = await orchestrator.ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(UninstallOutcome.Blocked, result.Outcome);
        Assert.Contains(result.Preflight.Issues, i => i.Check == PreflightCheck.InstallerBusy);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Runs_a_real_process_in_a_job_and_maps_its_exit_code()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        InstalledApp app = NsisApp() with
        {
            Scope = InstallScope.User,
            RegistryKey = null,
            InstallerKind = InstallerKind.Unknown,
            UninstallString = $"\"{cmd}\" /c exit 5",
            UninstallCommand = CommandLineParser.Parse($"\"{cmd}\" /c exit 5", SystemCommandEnvironment.Instance),
        };
        var runner = new JobUninstallRunner(new CompletionOptions { QuietWindow = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) }, NullLoggerFactory.Instance);
        var events = new List<UninstallProgress>();

        UninstallOrchestrator orchestrator = Orchestrator(runner, environment: SystemCommandEnvironment.Instance);
        UninstallResult result = await orchestrator.ExecuteAsync(
            orchestrator.Plan(app, UninstallMode.Interactive, TestContext.Current.CancellationToken),
            new SyncProgress(events),
            TestContext.Current.CancellationToken);

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(ExitMeaning.Failed, result.ExitMeaning);
        Assert.Contains(events, e => e.Event?.Kind == JobEventKind.Started);
        Assert.Equal([UninstallStage.Guardian, UninstallStage.Running, UninstallStage.Verifying], events.Where(e => e.Event is null).Select(e => e.Stage));
    }

    [Theory]
    [InlineData(0, InstallerKind.Unknown, CommandOrigin.UninstallString, ExitMeaning.Success)]
    [InlineData(null, InstallerKind.Unknown, CommandOrigin.UninstallString, ExitMeaning.Unknown)]
    [InlineData(3010, InstallerKind.InnoSetup, CommandOrigin.UninstallString, ExitMeaning.RebootRequired)]
    [InlineData(1641, InstallerKind.WixBurn, CommandOrigin.UninstallString, ExitMeaning.RebootInitiated)]
    [InlineData(1618, InstallerKind.Unknown, CommandOrigin.WindowsInstaller, ExitMeaning.InstallerBusy)]
    [InlineData(1602, InstallerKind.WixBurn, CommandOrigin.QuietUninstallString, ExitMeaning.Cancelled)]
    [InlineData(1602, InstallerKind.InnoSetup, CommandOrigin.UninstallString, ExitMeaning.Failed)]
    [InlineData(1, InstallerKind.Nsis, CommandOrigin.UninstallString, ExitMeaning.Cancelled)]
    [InlineData(1, InstallerKind.Squirrel, CommandOrigin.UninstallString, ExitMeaning.Failed)]
    public void Interprets_exit_codes_per_installer(int? code, InstallerKind kind, CommandOrigin origin, ExitMeaning expected) =>
        Assert.Equal(expected, UninstallerExitCodes.Interpret(code, kind, origin));

    [Theory]
    [InlineData(false, ExitMeaning.Failed, UninstallOutcome.Removed)]
    [InlineData(true, ExitMeaning.Success, UninstallOutcome.StillInstalled)]
    [InlineData(null, ExitMeaning.Success, UninstallOutcome.Unverified)]
    [InlineData(null, ExitMeaning.Failed, UninstallOutcome.Failed)]
    [InlineData(true, ExitMeaning.InstallerBusy, UninstallOutcome.Failed)]
    [InlineData(true, ExitMeaning.RebootInitiated, UninstallOutcome.PendingReboot)]
    public void Decides_the_outcome_from_verification_first(bool? stillRegistered, ExitMeaning meaning, UninstallOutcome expected) =>
        Assert.Equal(expected, UninstallOrchestrator.Decide(meaning, stillRegistered));

    private static InstalledApp NsisApp()
    {
        const string Uninstaller = @"C:\Program Files\Acme\uninst.exe";
        var environment = new FakeEnvironment(Uninstaller);
        return TestApps.App(InstallerKind.Nsis, $"\"{Uninstaller}\"", environment: environment) with { RegistryKey = Key, InstallLocation = null };
    }

    private UninstallOrchestrator Orchestrator(
        IUninstallRunner runner,
        IPackageRemover? packages = null,
        FakePreflightEnvironment? preflight = null,
        ICommandEnvironment? environment = null)
    {
        _state.Keys.Add(Key);
        environment ??= new FakeEnvironment(@"C:\Program Files\Acme\uninst.exe");
        return new UninstallOrchestrator(
            new CommandLineResolver(environment, new FakeLocations()),
            new UninstallPreflight(preflight ?? new FakePreflightEnvironment(), TestApps.Guard, NullLogger<UninstallPreflight>.Instance),
            new FingerprintCollector(TestApps.Guard, new EmptyTraceSource(), environment, NullLogger<FingerprintCollector>.Instance),
            new FingerprintStore(_store, NullLogger<FingerprintStore>.Instance),
            new RecordingGuardian(_calls),
            runner,
            packages ?? new FakePackages(_calls),
            _state,
            NullLogger<UninstallOrchestrator>.Instance);
    }

    private sealed class SyncProgress(List<UninstallProgress> events) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value) => events.Add(value);
    }
}
