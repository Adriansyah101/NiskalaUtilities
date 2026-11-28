using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Rollback;
using Niskala.UI.Services;
using Niskala.Uninstall;
using Niskala.Uninstall.Batch;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Leftovers;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Steam;

namespace Niskala.UI.ViewModels;

public enum FlowKind
{
    None,
    Uninstall,
    Force,
    Steam,
    Batch,
}

public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly UiServices _services;
    private readonly Dictionary<int, bool> _processes = [];
    private readonly Dictionary<string, CleanResult> _cleaned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _batchInto = new(StringComparer.OrdinalIgnoreCase);
    private UninstallOrchestrator? _orchestrator;
    private UninstallPlan? _plan;
    private InstallLog? _installLog;
    private LogRollback? _logRollback;
    private RollbackPlan? _rollbackPlan;
    private LeftoverCleaner? _cleaner;
    private ForceRemover? _forceRemover;
    private ForcePlan? _forcePlan;
    private SteamUninstaller? _steam;
    private SteamPlan? _steamPlan;
    private BatchUninstaller? _batch;
    private IReadOnlyList<BatchItem> _batchItems = [];
    private IReadOnlyList<BatchResult> _batchResults = [];
    private BatchReview? _batchReview;
    private LeftoverStages? _stages;
    private LeftoverStage? _stage;
    private InstalledApp? _app;
    private Fingerprint? _fingerprint;
    private string? _transactionId;
    private CancellationTokenSource? _wait;
    private int _run;

    internal InspectorViewModel(UiServices services) => _services = services;

    public event Action? LockChanged;

    public TimelineStepCollection Steps { get; } = [];

    public ActivityRowCollection Activity { get; } = [];

    public LeftoverReviewViewModel Review { get; } = new();

    public FlowGate Gate { get; } = new();

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScrim))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Subtitle), nameof(Location), nameof(HasLocation), nameof(Installed), nameof(Command), nameof(Website), nameof(HasWebsite), nameof(Facts), nameof(Notice), nameof(CanUninstall), nameof(CanForce))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(ForceCommand))]
    public partial AppItem? Item { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstall), nameof(Notice))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    public partial bool InInventory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUninstall), nameof(CanForce), nameof(IsDetails), nameof(ShowDone))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(ForceCommand), nameof(CloseFlowCommand), nameof(RescanCommand), nameof(CleanStageCommand), nameof(SkipStageCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetails), nameof(IsFlow), nameof(ShowScrim), nameof(IsSteam), nameof(IsBatch), nameof(ShowDone), nameof(Name), nameof(Subtitle), nameof(LockNotice))]
    public partial FlowKind Flow { get; set; }

    [ObservableProperty]
    public partial bool IsWide { get; set; }

    [ObservableProperty]
    public partial bool Silent { get; set; }

    [ObservableProperty]
    public partial bool RestorePoint { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDone))]
    public partial bool AwaitingStart { get; set; }

    [ObservableProperty]
    public partial string StartTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StartText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TextList SteamChoices { get; set; } = [];

    [ObservableProperty]
    public partial int SteamChoice { get; set; }

    [ObservableProperty]
    public partial bool SteamPermanent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDone))]
    public partial bool CanStopWaiting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDone), nameof(ShowLog), nameof(ShowForceActions), nameof(ShowStageActions))]
    public partial bool ShowReview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForceActions), nameof(ShowStageActions), nameof(StagePrimaryText), nameof(StageSecondaryText))]
    public partial ReviewScope CleanupStep { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial bool CanRescan { get; set; }

    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TextList EmptyFolders { get; set; } = [];

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Phase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Metrics { get; set; } = string.Empty;

    public string Name => Flow == FlowKind.Batch ? S.BatchTitle(_batchItems.Count) : Item?.Name ?? string.Empty;

    public string Subtitle => Flow == FlowKind.Batch ? string.Join(", ", _batchItems.Select(i => i.App.DisplayName ?? i.App.Id)) : Item?.Subtitle ?? string.Empty;

    public string Location => Item?.App.InstallLocation is { } folder
        ? folder + (Item.App.InstallLocationIsEstimate ? $" ({S.Estimated})" : string.Empty)
        : S.Unknown;

    public bool HasLocation => Item?.App.InstallLocation is { } folder && Directory.Exists(folder);

    public string Installed => Item?.InstalledText is { Length: > 0 } date ? date : S.Unknown;

    public string Command => Item?.App switch
    {
        { InstallerKind: InstallerKind.Steam } => S.SteamDirectCommand,
        { Package: { } package } => package.FullName,
        { MsiProductCode: { } code } => $"msiexec.exe /x {code:B}" + (Silent ? " /qn /norestart" : string.Empty),
        { QuietUninstallString: { } quiet } when Silent => quiet,
        { UninstallString: { } command } => command,
        _ => S.NoUninstallCommand,
    };

    public string? Website => Item?.App is { } app ? new[] { app.UrlInfoAbout, app.HelpLink }.FirstOrDefault(ShellLauncher.IsLink) : null;

    public bool HasWebsite => Website is not null;

    public TextList Facts => Item?.App is not { } app ? [] :
    [
        app.Scope == InstallScope.User ? S.ScopeUser : S.ScopeMachine,
        Item.SourceText,
        S.Installer(app.InstallerKind),
    ];

    public string Notice => Item?.App switch
    {
        null => string.Empty,
        { IsProtected: true } => S.ProtectedApp,
        { InstallerKind: InstallerKind.Steam } => string.Empty,
        { IsOrphan: true } => S.OrphanApp,
        { IsUninstallerMissing: true } => S.UninstallerMissing,
        _ when !InInventory => S.NotInInventory,
        _ => string.Empty,
    };

    public bool CanUninstall => Item is { App: { IsProtected: false } app } && (!app.IsOrphan || app.InstallerKind == InstallerKind.Steam) && InInventory && !IsBusy;

    public bool CanForce => Item is { App.IsProtected: false } && !IsBusy;

    public bool IsDetails => Flow == FlowKind.None;

    public bool IsFlow => Flow != FlowKind.None;

    public bool ShowScrim => IsOpen && IsFlow;

    public bool IsSteam => Flow == FlowKind.Steam;

    public bool IsBatch => Flow == FlowKind.Batch;

    public bool ShowLog => !ShowReview;

    public bool ShowDone => IsFlow && !IsBusy && !AwaitingStart && !ShowReview && !CanStopWaiting;

    public bool IsLocked => Gate.IsLocked;

    public bool ShowClose => Gate.ShowsCloseButton;

    public string LockNotice => IsLocked && Flow != FlowKind.Force ? S.LockNotice : string.Empty;

    public bool ShowForceActions => ShowReview && CleanupStep == ReviewScope.All;

    public bool ShowStageActions => ShowReview && CleanupStep != ReviewScope.All;

    public string StagePrimaryText => CleanupStep == ReviewScope.Registry ? S.CleanRegistry : S.CleanFilesContinue;

    public string StageSecondaryText => CleanupStep == ReviewScope.Registry ? S.FinishWithoutDeleting : S.SkipFiles;

    public void Show(AppItem item, bool inInventory = true)
    {
        if ((IsBusy || IsLocked) && Item is not null && Item.Id != item.Id)
        {
            IsOpen = true;
            Error = S.FinishRunningFirst;
            return;
        }

        if (Item?.Id != item.Id)
        {
            ResetFlow();
        }

        Error = string.Empty;
        InInventory = inInventory;
        Item = item;
        IsOpen = true;
    }

    public bool Leave(FlowExit exit)
    {
        switch (Gate.Request(exit))
        {
            case FlowExitResult.Refuse:
                return false;
            case FlowExitResult.CancelFlow:
                CancelFlow();
                return true;
            default:
                return true;
        }
    }

    partial void OnSilentChanged(bool value) => OnPropertyChanged(nameof(Command));

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            return;
        }

        switch (Gate.Request(FlowExit.Backdrop))
        {
            case FlowExitResult.Refuse:
                _services.Post(() => IsOpen = true);
                break;
            case FlowExitResult.CancelFlow:
                CancelFlow();
                break;
            default:
                if (!IsBusy && Flow != FlowKind.None && !ShowReview && !AwaitingStart)
                {
                    ResetFlow();
                }

                break;
        }
    }

    [RelayCommand(CanExecute = nameof(ShowClose))]
    private void Close() => IsOpen = false;

    [RelayCommand(CanExecute = nameof(CanCloseFlow))]
    private void CloseFlow() => ResetFlow();

    private bool CanCloseFlow() => !IsBusy && !IsLocked;

    [RelayCommand]
    private void OpenFolder()
    {
        if (Item?.App.InstallLocation is { } folder)
        {
            Report(ShellLauncher.OpenFolder(folder));
        }
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Website is { } link)
        {
            Report(ShellLauncher.OpenLink(link));
        }
    }

    [RelayCommand]
    private void ShowHistory()
    {
        if (IsLocked)
        {
            return;
        }

        _services.History.Select(_transactionId);
        _services.Navigate(Section.History);
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        InstalledApp app = Item!.App;
        if (app.InstallerKind == InstallerKind.Steam)
        {
            await PlanSteamAsync(app);
            return;
        }

        Begin(FlowKind.Uninstall, app, S.StepFingerprint, S.StepRestorePoint, S.StepProcessTree, S.StepScan, S.StepFiles, S.StepRegistry);
        int run = _run;
        IsBusy = true;
        Steps[0].Start(S.Collecting);
        try
        {
            _orchestrator = _services.Host.CreateUninstallOrchestrator(RestorePoint);
            UninstallMode mode = Silent ? UninstallMode.Silent : UninstallMode.Interactive;
            UninstallOrchestrator orchestrator = _orchestrator;
            UninstallPlan plan = await Task.Run(() => orchestrator.Plan(app, mode));
            InstallLog? installLog = await Task.Run(() => _services.Host.CreateInstallLogStore().FindForApp(app.Id));
            if (run != _run)
            {
                return;
            }

            _plan = plan;
            _fingerprint = plan.Fingerprint;
            _installLog = installLog;
            Steps[0].Finish(S.FingerprintDetail(
                _fingerprint.Files?.Files ?? 0,
                _fingerprint.Services.Count + _fingerprint.RunEntries.Count + _fingerprint.Shortcuts.Count + _fingerprint.ScheduledTasks.Count));

            Warn([.. plan.Resolution.Warnings.Select(w => w.Message), .. plan.Preflight.Issues.Select(i => i.Message)]);
            if (_installLog is not null)
            {
                Log(ActivityKind.Info, S.InstallLogFound(_installLog.Id));
            }

            if (!plan.Preflight.CanProceed || !plan.Resolution.CanRun)
            {
                Steps[0].Fail(plan.Resolution.CanRun ? S.Blocked : S.NoUninstallCommand);
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
                return;
            }

            StartTitle = S.StartTitle;
            StartText = plan.Resolution.Command?.CommandLine ?? plan.Resolution.PackageFullName ?? string.Empty;
            AwaitingStart = true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                Steps[0].Fail(ex.Message);
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StartBatchAsync(IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        if (apps.Count == 0)
        {
            return;
        }

        if (IsBusy || IsLocked)
        {
            IsOpen = true;
            Error = S.FinishRunningFirst;
            return;
        }

        Error = string.Empty;
        Item = null;
        InInventory = true;
        Begin(FlowKind.Batch, apps[0], [.. apps.Select(a => S.BatchStep(a.DisplayName ?? a.Id)), S.StepScan, S.StepFiles, S.StepRegistry]);
        int run = _run;
        IsBusy = true;
        Log(ActivityKind.Info, S.Collecting);
        try
        {
            _batch = _services.Host.CreateBatchUninstaller();
            BatchUninstaller batch = _batch;
            UninstallMode mode = Silent ? UninstallMode.Silent : UninstallMode.Interactive;
            IReadOnlyList<BatchItem> planned = await Task.Run(() => batch.Plan(apps, mode));
            if (run != _run)
            {
                return;
            }

            _batchItems = planned;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Subtitle));
            for (int i = 0; i < _batchItems.Count; i++)
            {
                BatchItem item = _batchItems[i];
                if (!item.CanRun)
                {
                    Steps[i].Skip(S.BatchBlocked(item.Block, item.Detail));
                }

                Warn([.. item.Plan?.Resolution.Warnings.Select(w => $"{item.App.DisplayName ?? item.App.Id}: {w.Message}") ?? []]);
            }

            List<BatchItem> runnable = [.. _batchItems.Where(i => i.CanRun)];
            if (runnable.Count == 0)
            {
                SkipFrom(_batchItems.Count, S.NotRun);
                EnterStage(FlowStage.Finished);
                return;
            }

            StartTitle = S.BatchStartTitle(runnable.Count);
            StartText = string.Join(Environment.NewLine, runnable.Select(i => $"{i.App.DisplayName ?? i.App.Id} · {i.Plan?.Resolution.Command?.CommandLine ?? i.Plan?.Resolution.PackageFullName ?? S.BatchSteamDirect}"));
            AwaitingStart = true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                Steps[0].Fail(ex.Message);
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelStart() => CancelFlow();

    [RelayCommand]
    private Task StartAsync() => Flow switch
    {
        FlowKind.Steam => RunSteamAsync(),
        FlowKind.Batch => RunBatchAsync(),
        _ => RunUninstallerAsync(),
    };

    private async Task RunUninstallerAsync()
    {
        if (_plan is not { } plan || _orchestrator is not { } orchestrator)
        {
            return;
        }

        int run = _run;
        AwaitingStart = false;
        EnterStage(FlowStage.Removing);
        IsBusy = true;
        var wait = new CancellationTokenSource();
        _wait = wait;
        CanStopWaiting = true;
        var progress = new Progress<UninstallProgress>(p =>
        {
            if (run == _run)
            {
                OnUninstallProgress(p);
            }
        });
        CancellationToken token = wait.Token;
        try
        {
            UninstallResult result = await Task.Run(() => orchestrator.ExecuteAsync(plan, progress, token));
            if (run != _run)
            {
                return;
            }

            CanStopWaiting = false;
            if (result.Checkpoint?.RestorePoint is { } point)
            {
                Steps[1].Finish(point.Created ? S.RestorePointCreated(point.Description) : point.Note ?? S.RestorePointSkipped);
            }
            else if (Steps[1].State != StepState.Done)
            {
                Steps[1].Skip(RestorePoint ? S.RestorePointSkipped : S.RestorePointOff);
            }

            _transactionId = result.Checkpoint?.TransactionId;
            bool finished = result.Outcome is UninstallOutcome.Removed or UninstallOutcome.PendingReboot or UninstallOutcome.StaleEntry or UninstallOutcome.Unverified;
            string outcome = string.Join(" ", new[] { S.Outcome(result.Outcome) }.Concat(result.Notes));
            if (!finished)
            {
                Steps[2].Fail(outcome);
                SkipFrom(3, S.NotRun);
                CanRescan = true;
                EnterStage(FlowStage.Finished);
                await _services.Apps.LoadAsync();
                return;
            }

            Steps[2].Finish(outcome);
            await ScanLeftoversAsync();
        }
        catch (OperationCanceledException)
        {
            if (run == _run)
            {
                Steps[2].Skip(S.StoppedWaiting);
                SkipFrom(3, S.NotRun);
                CanRescan = true;
                EnterStage(FlowStage.Finished);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                FailFlow(ex);
            }
        }
        finally
        {
            if (run == _run)
            {
                CanStopWaiting = false;
            }

            IsBusy = false;
            if (_wait == wait)
            {
                _wait = null;
            }

            wait.Dispose();
        }
    }

    [RelayCommand]
    private void StopWaiting() => TaskViewModel.Stop(_wait);

    [RelayCommand(CanExecute = nameof(CanRunRescan))]
    private async Task RescanAsync()
    {
        int run = _run;
        IsBusy = true;
        try
        {
            await (Flow == FlowKind.Batch ? ScanBatchAsync() : ScanLeftoversAsync());
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                FailFlow(ex);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunRescan() => CanRescan && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanForce))]
    private async Task ForceAsync()
    {
        InstalledApp app = Item!.App;
        Begin(FlowKind.Force, app, S.StepFingerprintScan, S.StepReview, S.StepStopProcesses, S.StepRestorePoint, S.StepRemove);
        int run = _run;
        IsBusy = true;
        Steps[0].Start(S.Collecting);
        try
        {
            _forceRemover = _services.Host.CreateForceRemover();
            IReadOnlyList<InstalledApp> installed = _services.Apps.Inventory;
            bool inInventory = InInventory;
            ForceRemover remover = _forceRemover;
            ForcePlan plan = await Task.Run(() => remover.Plan(app, inInventory, installed));
            if (run != _run)
            {
                return;
            }

            _forcePlan = plan;
            _fingerprint = plan.Fingerprint;
            Steps[0].Finish(S.ForcePlanDetail(plan.Leftovers.Items.Count, plan.Processes.Count, plan.Services.Count));
            foreach (RunningProcess process in plan.Processes)
            {
                _processes[process.Id] = true;
                Log(ActivityKind.ProcessStarted, S.ProcessToStop(process.Name, process.Id));
            }

            HashSet<LeftoverItem> preselected = [.. ForceRemover.Select(app, plan.Leftovers, includeReview: false)];
            Steps[1].Start(S.ChooseWhatGoes);
            Review.Load(plan.Leftovers.Items, preselected.Contains);
            CleanupStep = ReviewScope.All;
            Warn(plan.Leftovers.Notes);
            OpenReview();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                Steps[0].Fail(ex.Message);
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (Flow != FlowKind.Force)
        {
            return;
        }

        IReadOnlyList<LeftoverItem> chosen = Review.Checked;
        bool permanent = Review.Permanent;
        IsBusy = true;
        ShowReview = false;
        EnterStage(FlowStage.Cleanup);
        try
        {
            CleanResult result = await RunForceAsync(chosen, permanent);
            _transactionId = result.Transaction.Id;
            OfferEmptyFolders([result]);
            Result = Summary([result]);
            Steps[^1].Finish(Result);
            await _services.Apps.LoadAsync();
            _services.History.Invalidate();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Steps[^1].Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
            EnterStage(FlowStage.Finished);
        }
    }

    [RelayCommand]
    private void SkipClean()
    {
        ShowReview = false;
        Steps[1].Skip(S.LeftoversKept);
        SkipFrom(2, S.LeftoversKept);
        EnterStage(FlowStage.Finished);
    }

    [RelayCommand(CanExecute = nameof(CanActOnStage))]
    private async Task CleanStageAsync()
    {
        if (_stage is not { } stage || _stages is null)
        {
            return;
        }

        int run = _run;
        IReadOnlyList<LeftoverItem> chosen = Review.Checked;
        bool permanent = Review.Permanent;
        TimelineStep step = StepFor(stage);
        bool reverts = stage == LeftoverStage.Registry && (_rollbackPlan?.Reverts.Count ?? 0) > 0;
        if (chosen.Count == 0 && !reverts)
        {
            await SkipStageAsync();
            return;
        }

        IsBusy = true;
        ShowReview = false;
        try
        {
            IReadOnlyList<CleanResult> results = await RunStageAsync(stage, chosen, permanent);
            if (run != _run)
            {
                return;
            }

            foreach (CleanResult result in results)
            {
                _cleaned[result.Transaction.Id] = result;
                _batchInto[result.Transaction.AppId] = result.Transaction.Id;
            }

            step.Finish(StageSummary(results, stage));
            _services.History.Invalidate();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run != _run)
            {
                return;
            }

            step.Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await AdvanceAsync(stage);
    }

    [RelayCommand(CanExecute = nameof(CanActOnStage))]
    private async Task SkipStageAsync()
    {
        if (_stage is not { } stage)
        {
            return;
        }

        ShowReview = false;
        StepFor(stage).Skip(stage == LeftoverStage.Files ? S.FilesKept : S.RegistryKept);
        await AdvanceAsync(stage);
    }

    private bool CanActOnStage() => !IsBusy;

    [RelayCommand]
    private async Task RemoveEmptyFoldersAsync()
    {
        if (_cleaner is not { } cleaner || _transactionId is not { } id)
        {
            return;
        }

        IReadOnlyList<string> folders = EmptyFolders;
        EmptyFolders = [];
        try
        {
            TransactionRecord record = await Task.Run(() => cleaner.RemoveEmptyFolders(id, folders));
            int removed = record.Items.Count(i => i.EmptyFolder && i.Status == RemovalStatus.Removed);
            Result = $"{Result} {S.EmptyFoldersRemoved(removed)}";
            Log(ActivityKind.Success, S.EmptyFoldersRemoved(removed));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private async Task PlanSteamAsync(InstalledApp app)
    {
        Begin(FlowKind.Steam, app, S.StepSteamFind, S.StepRestorePoint, S.StepRemove, S.StepScan, S.StepFiles, S.StepRegistry);
        int run = _run;
        IsBusy = true;
        Steps[0].Start(S.SteamLooking);
        try
        {
            _steam = _services.Host.CreateSteamUninstaller();
            SteamUninstaller steam = _steam;
            (SteamPlan? plan, string? problem) = await Task.Run(() => (steam.Plan(app, out string? reason), reason));
            if (run != _run)
            {
                return;
            }

            if (plan is null)
            {
                Steps[0].Fail(S.SteamNoPlan(problem ?? string.Empty));
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
                return;
            }

            _steamPlan = plan;
            _fingerprint = plan.Fingerprint;
            Steps[0].Finish(plan.Game is { } game ? S.SteamFound(game.Library, game.Manifest.SizeOnDisk is { } size ? Format.Bytes(size) : S.Unknown) : S.SteamFilesGone);
            Log(plan.SteamRunning ? ActivityKind.Warning : ActivityKind.Info, plan.SteamRunning ? S.SteamRunning : S.SteamClosed);
            StartTitle = S.SteamStartTitle;
            StartText = string.Join(Environment.NewLine, plan.Items.Select(i => i.Kind == LeftoverKind.RegistryKey ? i.Path : $"{i.Path}{(i.Bytes is { } bytes ? $"  ({Format.Bytes(bytes)})" : string.Empty)}"));
            TextList direct = [plan.SteamRunning ? S.SteamCloseThenRemove : plan.Game is null ? S.SteamDirectRecords : S.SteamDirect];
            SteamChoices = plan.Game is null ? direct : [.. direct, S.SteamVia];
            SteamChoice = 0;
            SteamPermanent = false;
            AwaitingStart = true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                Steps[0].Fail(ex.Message);
                SkipFrom(1, S.NotRun);
                EnterStage(FlowStage.Finished);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunSteamAsync()
    {
        if (_steamPlan is not { } plan || _steam is not { } steam)
        {
            return;
        }

        int run = _run;
        AwaitingStart = false;
        IsBusy = true;
        SteamRemoval removal = SteamChoice == 1 ? SteamRemoval.ViaSteam : plan.SteamRunning ? SteamRemoval.CloseSteam : SteamRemoval.Direct;
        EnterStage(removal == SteamRemoval.ViaSteam ? FlowStage.Removing : FlowStage.Cleanup);
        var options = new SteamOptions { Removal = removal, Permanent = SteamPermanent, CreateRestorePoint = RestorePoint };
        if (removal == SteamRemoval.CloseSteam)
        {
            Log(ActivityKind.Info, S.SteamClosing);
        }

        var progress = new Progress<CleanProgress>(p =>
        {
            if (run == _run)
            {
                OnCleanProgress(p, restoreStep: 1, removeStep: 2);
            }
        });
        try
        {
            SteamResult result = await Task.Run(() => steam.ExecuteAsync(plan, options, progress));
            if (run != _run)
            {
                return;
            }

            switch (result.Outcome)
            {
                case SteamOutcome.Removed:
                    CleanResult clean = result.Clean!;
                    _transactionId = clean.Transaction.Id;
                    _cleaner = _services.Host.CreateLeftoverCleaner();
                    FinishRestoreStep(clean, 1);
                    Steps[2].Finish(Summary([clean]));
                    OfferEmptyFolders([clean]);
                    _services.History.Invalidate();
                    await ScanLeftoversAsync();
                    break;
                case SteamOutcome.HandedToSteam:
                    Steps[1].Skip(S.NotRun);
                    Steps[2].Finish(S.SteamHandedOver);
                    SkipFrom(3, S.SteamScanLater);
                    CanRescan = true;
                    EnterStage(FlowStage.Finished);
                    break;
                default:
                    Steps[1].Skip(S.NotRun);
                    Steps[2].Fail(result.Outcome == SteamOutcome.SteamRunning ? S.SteamStillRunning : S.SteamNotStarted(result.Error));
                    SkipFrom(3, S.NotRun);
                    EnterStage(FlowStage.Finished);
                    break;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                FailFlow(ex);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunBatchAsync()
    {
        if (_batch is not { } batch)
        {
            return;
        }

        int run = _run;
        AwaitingStart = false;
        EnterStage(FlowStage.Removing);
        IsBusy = true;
        var wait = new CancellationTokenSource();
        _wait = wait;
        CanStopWaiting = true;
        CancellationToken token = wait.Token;
        IReadOnlyList<BatchItem> items = _batchItems;
        bool restorePoint = RestorePoint;
        var progress = new Progress<BatchProgress>(p =>
        {
            if (run == _run)
            {
                OnBatchProgress(p);
            }
        });
        try
        {
            IReadOnlyList<BatchResult> results = await Task.Run(() => batch.RunAsync(items, restorePoint, progress, token));
            if (run != _run)
            {
                _services.History.Invalidate();
                return;
            }

            _batchResults = results;
            CanStopWaiting = false;
            _services.History.Invalidate();
            if (!_batchResults.Any(r => r.Removed))
            {
                SkipFrom(items.Count, S.NotRun);
                CanRescan = true;
                EnterStage(FlowStage.Finished);
                await _services.Apps.LoadAsync();
                return;
            }

            await ScanBatchAsync();
        }
        catch (OperationCanceledException)
        {
            if (run == _run)
            {
                SkipFrom(0, S.StoppedWaiting);
                CanRescan = true;
                EnterStage(FlowStage.Finished);
            }

            await _services.Apps.LoadAsync();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (run == _run)
            {
                FailFlow(ex);
            }
        }
        finally
        {
            if (run == _run)
            {
                CanStopWaiting = false;
            }

            IsBusy = false;
            if (_wait == wait)
            {
                _wait = null;
            }

            wait.Dispose();
        }
    }

    private void OnBatchProgress(BatchProgress progress)
    {
        TimelineStep step = Steps[progress.Index];
        switch (progress)
        {
            case { Step: BatchStep.Started, Uninstall.Event: { } job }:
                OnJobEvent(job, step);
                break;
            case { Step: BatchStep.Started, Uninstall.Stage: UninstallStage.Verifying }:
                step.Detail = S.Verifying;
                break;
            case { Step: BatchStep.Started, Clean.Item: { } removed }:
                step.Detail = removed.Path;
                break;
            case { Step: BatchStep.Started } when !step.IsActive:
                step.Start(S.UninstallerRunning);
                break;
            case { Step: BatchStep.Finished, Result: { } result }:
                step.Finish(Outcome(result));
                break;
            case { Step: BatchStep.Failed, Result: { } result }:
                step.Fail(Outcome(result));
                break;
        }
    }

    private string Outcome(BatchResult result) => result switch
    {
        { Uninstall: { } ran } => string.Join(" ", new[] { S.Outcome(ran.Outcome) }.Concat(ran.Notes)),
        { Steam: { Outcome: SteamOutcome.Removed, Clean: { } clean } } => Summary([clean]),
        { Steam: { } steam } => S.SteamNotStarted(steam.Error),
        _ => S.NotRun,
    };

    private async Task ScanBatchAsync()
    {
        int run = _run;
        CanRescan = false;
        EnterStage(FlowStage.Cleanup);
        TimelineStep scan = Steps[^3];
        scan.Start(S.Scanning);
        await _services.Apps.LoadAsync();
        IReadOnlyList<InstalledApp> installed = _services.Apps.Inventory;
        BatchUninstaller batch = _batch!;
        IReadOnlyList<BatchResult> results = _batchResults;
        BatchReview review = await Task.Run(() => batch.Scan(results, installed));
        if (run != _run)
        {
            return;
        }

        _batchReview = review;
        Warn(review.Notes);
        scan.Finish(S.LeftoversFound(review.Items.Count, review.Excluded));
        await BeginStagesAsync(LeftoverStages.Split(review.Items));
    }

    private async Task ScanLeftoversAsync()
    {
        int run = _run;
        CanRescan = false;
        EnterStage(FlowStage.Cleanup);
        TimelineStep scan = Steps[^3];
        scan.Start(S.Scanning);
        await _services.Apps.LoadAsync();
        if (run != _run)
        {
            return;
        }

        IReadOnlyList<InstalledApp> installed = _services.Apps.Inventory;
        _cleaner = _services.Host.CreateLeftoverCleaner();
        IReadOnlyList<LeftoverItem> items;
        int reverts = 0;
        if (_installLog is { } log)
        {
            _logRollback = _services.Host.CreateLogRollback(_cleaner);
            LogRollback rollback = _logRollback;
            RollbackPlan plan = await Task.Run(() => rollback.Plan(log));
            if (run != _run)
            {
                return;
            }

            _rollbackPlan = plan;
            items = plan.Items;
            reverts = plan.Reverts.Count;
            Warn([.. plan.Warnings, .. plan.Kept]);
            scan.Finish(S.FromInstallLog(items.Count, reverts));
        }
        else
        {
            InstalledApp app = _app!;
            Fingerprint fingerprint = _fingerprint!;
            LeftoverScanner scanner = _services.Host.CreateLeftoverScanner();
            LeftoverReport report = await Task.Run(() => scanner.Scan(app, fingerprint, installed));
            if (run != _run)
            {
                return;
            }

            items = report.Items;
            Warn(report.Notes);
            scan.Finish(S.LeftoversFound(items.Count, report.Excluded.Count));
        }

        await BeginStagesAsync(LeftoverStages.Split(items, reverts));
    }

    private async Task BeginStagesAsync(LeftoverStages stages)
    {
        _stages = stages;
        _cleaned.Clear();
        _batchInto.Clear();
        if (!stages.HasFiles)
        {
            Steps[^2].Skip(S.NoLeftoverFiles);
        }

        if (!stages.HasRegistry)
        {
            Steps[^1].Skip(S.NoLeftoverRegistry);
        }

        if (stages.First is { } first)
        {
            OpenStage(first);
            return;
        }

        await FinishCleanupAsync(S.NoLeftovers);
    }

    private void OpenStage(LeftoverStage stage)
    {
        _stage = stage;
        ReviewScope scope = stage == LeftoverStage.Files ? ReviewScope.Files : ReviewScope.Registry;
        StepFor(stage).Start(stage == LeftoverStage.Files ? S.ChooseFiles : S.ChooseRegistry);
        Review.Load(_stages!.Items(stage), i => i.Tier == ConfidenceTier.Safe, scope);
        CleanupStep = scope;
        UpdateMetrics();
        OpenReview();
    }

    private async Task AdvanceAsync(LeftoverStage stage)
    {
        if (_stages?.After(stage) is { } next)
        {
            OpenStage(next);
            return;
        }

        await FinishCleanupAsync(null);
    }

    private async Task FinishCleanupAsync(string? text)
    {
        int run = _run;
        _stage = null;
        ShowReview = false;
        List<CleanResult> results = [.. _cleaned.Values];
        if (results.Count > 0)
        {
            _transactionId = results[^1].Transaction.Id;
            OfferEmptyFolders(results);
            Result = Summary(results);
        }
        else
        {
            Result = text ?? S.LeftoversKept;
        }

        EnterStage(FlowStage.Finished);
        await _services.Apps.LoadAsync();
        if (run == _run && results.Count > 0)
        {
            _services.History.Invalidate();
        }
    }

    private async Task<IReadOnlyList<CleanResult>> RunStageAsync(LeftoverStage stage, IReadOnlyList<LeftoverItem> chosen, bool permanent)
    {
        int removeStep = Steps.IndexOf(StepFor(stage));
        var options = new CleanOptions { Permanent = permanent && stage == LeftoverStage.Files, CreateRestorePoint = false };
        int run = _run;
        var progress = new Progress<CleanProgress>(p =>
        {
            if (run == _run)
            {
                OnCleanProgress(p, restoreStep: -1, removeStep);
            }
        });
        _cleaner ??= _services.Host.CreateLeftoverCleaner();
        if (Flow == FlowKind.Batch)
        {
            BatchUninstaller batch = _batch!;
            BatchReview review = _batchReview!;
            Dictionary<string, string> into = new(_batchInto, StringComparer.OrdinalIgnoreCase);
            return await Task.Run(() => batch.CleanAsync(review, chosen, options, progress, into));
        }

        options = options with { Into = _cleaned.Keys.LastOrDefault() };
        if (_rollbackPlan is { } plan && _logRollback is { } rollback)
        {
            RollbackPlan picked = plan with { Items = chosen, Reverts = stage == LeftoverStage.Registry ? plan.Reverts : [] };
            return [await Task.Run(() => rollback.ExecuteAsync(picked, options, progress))];
        }

        LeftoverCleaner cleaner = _cleaner;
        InstalledApp app = _app!;
        Fingerprint fingerprint = _fingerprint!;
        return [await Task.Run(() => cleaner.CleanAsync(app, fingerprint, chosen, options, progress))];
    }

    private TimelineStep StepFor(LeftoverStage stage) => stage == LeftoverStage.Files ? Steps[^2] : Steps[^1];

    private async Task<CleanResult> RunForceAsync(IReadOnlyList<LeftoverItem> chosen, bool permanent)
    {
        ForcePlan plan = _forcePlan!;
        ForceRemover remover = _forceRemover!;
        _cleaner = _services.Host.CreateLeftoverCleaner();
        Steps[1].Finish(S.CheckedSummary(chosen.Count, Review.Total, null));
        Steps[2].Start(S.StoppingProcesses);
        var options = new ForceOptions { Permanent = permanent, CreateRestorePoint = RestorePoint, Chosen = chosen };
        var progress = new Progress<CleanProgress>(p =>
        {
            if (Steps[2].IsActive)
            {
                Steps[2].Finish(S.ProcessesStoppedShort);
            }

            OnCleanProgress(p, restoreStep: 3, removeStep: 4);
        });
        IReadOnlyList<InstalledApp> installed = _services.Apps.Inventory;
        ForceResult result = await Task.Run(() => remover.ExecuteAsync(plan, installed, options, progress));
        foreach (ProcessStop stop in result.Processes)
        {
            _processes[stop.Process.Id] = false;
            Log(ActivityKind.ProcessExited, S.ProcessStopped(stop.Process.Name, stop.Process.Id, stop.Result.Outcome.ToString()));
        }

        UpdateMetrics();
        if (Steps[2].IsActive)
        {
            Steps[2].Finish(S.ProcessesStopped(result.Processes.Count, result.Services.Count));
        }

        FinishRestoreStep(result.Clean, 3);
        return result.Clean;
    }

    private void FinishRestoreStep(CleanResult result, int index)
    {
        if (!Steps[index].IsDone && !Steps[index].IsSkipped)
        {
            Steps[index].Finish(result.Transaction.RestorePoint is { } point ? (point.Created ? S.RestorePointCreated(point.Description) : point.Note ?? S.RestorePointSkipped) : S.RestorePointOff);
        }
    }

    private void OnCleanProgress(CleanProgress progress, int restoreStep, int removeStep)
    {
        switch (progress.Stage)
        {
            case CleanStage.RestorePoint when restoreStep >= 0:
                Steps[restoreStep].Start(S.CreatingRestorePoint);
                break;
            case CleanStage.Backup:
                if (restoreStep >= 0 && Steps[restoreStep].IsActive)
                {
                    Steps[restoreStep].Finish(S.RestorePointRequested);
                }
                else if (restoreStep >= 0 && Steps[restoreStep].IsPending)
                {
                    Steps[restoreStep].Skip(S.RestorePointOff);
                }

                Steps[removeStep].Start(S.Stage(progress.Stage));
                break;
            default:
                if (!Steps[removeStep].IsActive)
                {
                    Steps[removeStep].Start(S.Stage(progress.Stage));
                }

                if (progress.Item is { } item)
                {
                    Steps[removeStep].Detail = item.Path;
                    Log(item.Status switch
                    {
                        RemovalStatus.Removed or RemovalStatus.Reverted or RemovalStatus.ScheduledAtReboot => ActivityKind.Success,
                        RemovalStatus.Failed => ActivityKind.Failure,
                        _ => ActivityKind.Info,
                    }, $"{S.Status(item.Status)} · {CleanTarget(item)}{(item.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty)}");
                }

                break;
        }
    }

    private void OnUninstallProgress(UninstallProgress progress)
    {
        switch (progress.Stage)
        {
            case UninstallStage.Guardian:
                Steps[1].Start(RestorePoint ? S.CreatingRestorePoint : S.BackingUp);
                break;
            case UninstallStage.Running when progress.Event is { } job:
                OnJobEvent(job, Steps[2]);
                break;
            case UninstallStage.Running:
                if (Steps[1].IsActive)
                {
                    Steps[1].Finish(RestorePoint ? S.RestorePointRequested : S.RestorePointOff);
                }

                Steps[2].Start(S.UninstallerRunning);
                break;
            case UninstallStage.Verifying:
                Steps[2].Detail = S.Verifying;
                break;
        }
    }

    private void OnJobEvent(JobEvent job, TimelineStep step)
    {
        string name = job.ImagePath is { } image ? Path.GetFileName(image) : $"PID {job.ProcessId}";
        switch (job.Kind)
        {
            case JobEventKind.Started:
                _processes[job.ProcessId] = true;
                Log(ActivityKind.ProcessStarted, S.ProcessStarted(name, job.ProcessId));
                break;
            case JobEventKind.Exited or JobEventKind.ExitedAbnormally:
                _processes[job.ProcessId] = false;
                Log(job.Kind == JobEventKind.Exited ? ActivityKind.ProcessExited : ActivityKind.Warning, S.ProcessEnded(name, job.ProcessId, job.Kind == JobEventKind.ExitedAbnormally));
                break;
            case JobEventKind.Empty:
                Log(ActivityKind.Info, S.JobEmpty);
                break;
        }

        step.Detail = S.ProcessesRunning(_processes.Count(p => p.Value));
        UpdateMetrics();
    }

    private void Begin(FlowKind kind, InstalledApp app, params string[] titles)
    {
        ResetFlow();
        Flow = kind;
        _app = app;
        for (int i = 0; i < titles.Length; i++)
        {
            var step = new TimelineStep(titles[i], i == titles.Length - 1);
            step.PropertyChanged += OnStepChanged;
            Steps.Add(step);
        }

        EnterStage(FlowStage.Preparing);
        IsWide = true;
        IsOpen = true;
        UpdatePhase();
    }

    private void EnterStage(FlowStage stage)
    {
        bool locked = Gate.IsLocked;
        if (!Gate.Enter(stage))
        {
            return;
        }

        CloseCommand.NotifyCanExecuteChanged();
        CloseFlowCommand.NotifyCanExecuteChanged();
        if (locked != Gate.IsLocked)
        {
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(ShowClose));
            OnPropertyChanged(nameof(LockNotice));
            LockChanged?.Invoke();
        }
    }

    private void CancelFlow()
    {
        bool removing = Gate.Stage == FlowStage.Removing;
        TaskViewModel.Stop(_wait);
        ResetFlow();
        Error = string.Empty;
        IsOpen = false;
        if (removing)
        {
            _ = _services.Apps.LoadAsync();
        }
    }

    private void FailFlow(Exception ex)
    {
        Steps.FirstOrDefault(s => s.IsActive || s.IsPending)?.Fail(ex.Message);
        SkipFrom(0, S.NotRun);
        ShowReview = false;
        _stage = null;
        EnterStage(FlowStage.Finished);
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TimelineStep step)
        {
            return;
        }

        if (e.PropertyName == nameof(TimelineStep.State))
        {
            string text = step.Detail.Length > 0 ? $"{step.Title}: {step.Detail}" : step.Title;
            switch (step.State)
            {
                case StepState.Active:
                    Log(ActivityKind.Info, text);
                    break;
                case StepState.Done:
                    Log(ActivityKind.Success, text);
                    break;
                case StepState.Failed:
                    Log(ActivityKind.Failure, text);
                    break;
                case StepState.Skipped:
                    Log(ActivityKind.Info, $"{step.Title}: {S.Skipped} · {step.Detail}");
                    break;
            }
        }

        UpdatePhase();
    }

    private void UpdatePhase()
    {
        TimelineStep? current = Steps.FirstOrDefault(s => s.IsActive) ?? Steps.LastOrDefault(s => !s.IsPending);
        Phase = current is null ? string.Empty : current.Detail.Length > 0 ? $"{current.Title} · {current.Detail}" : current.Title;
        Progress = Steps.Count == 0 ? 0 : Steps.Count(s => s.IsDone || s.IsFailed || s.IsSkipped) / (double)Steps.Count;
    }

    private void UpdateMetrics()
    {
        var parts = new List<string>();
        if (_processes.Count > 0)
        {
            parts.Add(S.ProcessMetric(_processes.Count(p => p.Value), _processes.Count));
        }

        if (Review.Total > 0)
        {
            parts.Add(S.LeftoverMetric(Review.Total));
        }

        Metrics = string.Join("   ·   ", parts);
    }

    private void OpenReview()
    {
        ShowReview = true;
        IsWide = true;
        IsOpen = true;
    }

    private void OfferEmptyFolders(IReadOnlyList<CleanResult> results)
    {
        EmptyFolders = [.. results.SelectMany(r => r.EmptyParents).Distinct(StringComparer.OrdinalIgnoreCase)];
        foreach (string folder in EmptyFolders)
        {
            Log(ActivityKind.Info, S.EmptyFolderLeft(folder));
        }
    }

    private void Warn(IEnumerable<string> messages)
    {
        foreach (string message in messages)
        {
            Log(ActivityKind.Warning, message);
        }
    }

    private void Log(ActivityKind kind, string text) => Activity.Add(new ActivityRow(DateTime.Now, kind, text));

    private void ResetFlow()
    {
        _run++;
        foreach (TimelineStep step in Steps)
        {
            step.PropertyChanged -= OnStepChanged;
        }

        Steps.Clear();
        Activity.Clear();
        _processes.Clear();
        _cleaned.Clear();
        _batchInto.Clear();
        Flow = FlowKind.None;
        AwaitingStart = false;
        ShowReview = false;
        CleanupStep = ReviewScope.All;
        CanRescan = false;
        CanStopWaiting = false;
        IsWide = false;
        Result = string.Empty;
        Phase = string.Empty;
        Progress = 0;
        Metrics = string.Empty;
        EmptyFolders = [];
        SteamChoices = [];
        _plan = null;
        _installLog = null;
        _rollbackPlan = null;
        _logRollback = null;
        _forcePlan = null;
        _steamPlan = null;
        _batch = null;
        _batchItems = [];
        _batchResults = [];
        _batchReview = null;
        _stages = null;
        _stage = null;
        _app = null;
        _fingerprint = null;
        _transactionId = null;
        EnterStage(FlowStage.Idle);
    }

    private void SkipFrom(int index, string detail)
    {
        for (int i = index; i < Steps.Count; i++)
        {
            if (Steps[i].IsPending || Steps[i].IsActive)
            {
                Steps[i].Skip(detail);
            }
        }
    }

    private string StageSummary(IReadOnlyList<CleanResult> results, LeftoverStage stage)
    {
        int Tally(RemovalStatus status) =>
            results.Sum(r => r.Transaction.Items.Count(i => i.Status == status && !i.EmptyFolder && LeftoverStages.StageOf(i.Kind) == stage));

        return S.CleanSummary(Tally(RemovalStatus.Removed), Tally(RemovalStatus.ScheduledAtReboot), Tally(RemovalStatus.Skipped), Tally(RemovalStatus.Failed), Tally(RemovalStatus.Reverted));
    }

    private string Summary(IReadOnlyList<CleanResult> results) =>
        S.CleanSummary(Count(results, RemovalStatus.Removed), Count(results, RemovalStatus.ScheduledAtReboot), Count(results, RemovalStatus.Skipped), Count(results, RemovalStatus.Failed), Count(results, RemovalStatus.Reverted));

    private void Report(int error) => Error = error == 0 ? string.Empty : S.OpenFailed(error);

    private static string CleanTarget(RemovalRecord record) => record.ValueName is null ? record.Path : $"{record.Path} → {record.ValueName}";

    private static int Count(IReadOnlyList<CleanResult> results, RemovalStatus status) => results.Sum(r => r.Transaction.Items.Count(i => i.Status == status));

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;
}
