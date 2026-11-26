using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian.Startup;
using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;
using Niskala.UI.Services;
using Niskala.UI.Views;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Picker;
using Niskala.Uninstall.Preflight;

namespace Niskala.UI.ViewModels;

public sealed partial class TargetPickerViewModel : ObservableObject
{
    private const uint AccentRgb = 0x7C9CF0;

    private readonly UiServices _services;
    private readonly PickerActions _actions = new(SystemProcessControl.Instance);
    private WindowPicker? _picker;
    private PickerOverlay? _overlay;
    private PickerCard? _card;

    internal TargetPickerViewModel(UiServices services) => _services = services;

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsPicking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTitle), nameof(PreviewDetail))]
    public partial PickerHit? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultTitle), nameof(ResultImage), nameof(ResultProcess), nameof(ResultProcessLabel), nameof(ResultApp), nameof(ResultMatch), nameof(CanRemove), nameof(RemoveLabel), nameof(HasResultLocation))]
    public partial PickerHit? Result { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopProcesses), nameof(ProcessesText))]
    public partial IReadOnlyList<RunningProcess> Running { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDisableStartup), nameof(StartupText))]
    public partial IReadOnlyList<StartupEntry> StartupEntries { get; set; } = [];

    [ObservableProperty]
    public partial bool ConfirmingStop { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string PreviewTitle => Preview is { } hit ? Title(hit) : S.PickerPointAt;

    public string PreviewDetail => Preview is { } hit ? AppLine(hit) : S.PickerHint;

    public bool HasResult => Result is not null;

    public string ResultTitle => Result is { } hit ? Title(hit) : string.Empty;

    public string ResultImage => Result?.ImagePath ?? S.Unknown;

    public string ResultProcess => Result switch
    {
        { ItemPath: { } item } => item,
        { ProcessId: not 0 } hit => $"PID {hit.ProcessId} · {S.TargetKind(hit.Kind)}",
        _ => string.Empty,
    };

    public string ResultProcessLabel => Result?.ItemPath is null ? S.LabelProcess : S.LabelItem;

    public string ResultApp => Result is { } hit ? AppLine(hit) : string.Empty;

    public string ResultMatch => Result is { } hit ? S.Match(hit.MatchedBy) : string.Empty;

    public bool HasResultLocation => (Result?.ItemPath ?? Result?.ImagePath) is not null;

    public bool CanRemove => Result is { App.IsProtected: false } || Result is { App: null, ImagePath: not null };

    public string RemoveLabel => Uninstallable(Result?.App) ? S.Uninstall : S.Force;

    public bool CanStopProcesses => Running.Count > 0;

    public string ProcessesText => Running.Count == 0 ? S.PickerNoProcess : string.Join(", ", Running.Select(p => $"{p.Name} ({p.Id})"));

    public bool CanDisableStartup => StartupEntries.Count > 0;

    public string StartupText => StartupEntries.Count == 0 ? S.PickerNoStartup : string.Join(", ", StartupEntries.Select(e => e.Name));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsPicking = true;
        Error = null;
        Notice = null;
        _services.HideMainWindow(true);
        _picker = new WindowPicker(SystemWindowProbe.Instance);
        await Task.Run(_picker.ReadDesktop);
        try
        {
            _card = new PickerCard(this);
            _overlay = PickerOverlay.Show(AccentRgb);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            Stop();
            return;
        }

        _overlay.Moved += OnMoved;
        _overlay.Picked += OnPicked;
        _overlay.Cancelled += Stop;
        _card.ShowBeside(_overlay.Handle, null);
        UpdatePassThrough();
    }

    private bool CanStart() => !IsPicking;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (IsPicking)
        {
            Stop();

            await Task.Yield();
        }

        if (Pickers.ProgramOrShortcut() is not { } file)
        {
            return;
        }

        IReadOnlyList<InstalledApp> apps = _services.Apps.Inventory;
        await ShowResultAsync(await Task.Run(() => TargetResolver.FromFile(file, apps)));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Result is not { } hit)
        {
            return;
        }

        if (hit.App is { } app)
        {
            _services.Apps.Open(app, Uninstallable(app) ? FlowKind.Uninstall : FlowKind.Force);
            return;
        }

        if (Path.GetDirectoryName(hit.ImagePath) is not { } folder)
        {
            return;
        }

        ForceTarget? target = ForceRemover.ForPath(folder, _services.Host.PathGuard, _services.Apps.Inventory, _services.Host.KnownRoots, out PathDecision decision);
        if (target is null)
        {
            Error = S.FolderRefused(folder, decision.Verdict);
            return;
        }

        _services.Apps.Open(target.App, FlowKind.Force);
    }

    [RelayCommand]
    private void AskStopProcesses() => ConfirmingStop = Running.Count > 0;

    [RelayCommand]
    private void CancelStopProcesses() => ConfirmingStop = false;

    [RelayCommand]
    private async Task StopProcessesAsync()
    {
        ConfirmingStop = false;
        IReadOnlyList<RunningProcess> running = Running;
        IReadOnlyList<ProcessStop> stops = await Task.Run(() => _actions.Stop(running));
        int stopped = stops.Count(s => s.Result.Outcome is TerminateOutcome.Terminated or TerminateOutcome.Exited);
        Notice = S.PickerStopped(stopped, stops.Count);
        Error = stopped == stops.Count ? null : string.Join(" ", stops.Where(s => s.Result.Outcome is not (TerminateOutcome.Terminated or TerminateOutcome.Exited)).Select(s => $"{s.Process.Name}: {s.Result.Outcome}"));
        await RefreshActionsAsync();
    }

    [RelayCommand]
    private async Task DisableStartupAsync()
    {
        IReadOnlyList<StartupEntry> entries = StartupEntries;
        StartupCatalog catalog = _services.Host.CreateStartupCatalog();
        List<(StartupEntry Entry, int Error)> results = await Task.Run(() => entries.Select(e => (e, catalog.SetEnabled(e, enabled: false))).ToList());
        int disabled = results.Count(r => r.Error == 0);
        Notice = S.PickerStartupDisabled(disabled);
        Error = disabled == results.Count ? null : string.Join(" ", results.Where(r => r.Error != 0).Select(r => S.StartupFailed(r.Entry.Name, r.Error)));
        _services.Resources.RefreshCommand.Execute(null);
        await RefreshActionsAsync();
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if ((Result?.ItemPath ?? Result?.ImagePath) is { } path)
        {
            int error = ShellLauncher.ShowItem(path);
            Error = error == 0 ? null : S.OpenFailed(error);
        }
    }

    public void Stop()
    {
        if (_overlay is not null)
        {
            _overlay.Moved -= OnMoved;
            _overlay.Picked -= OnPicked;
            _overlay.Cancelled -= Stop;
            _overlay.Dispose();
            _overlay = null;
        }

        _card?.Close();
        _card = null;
        _picker = null;
        Preview = null;
        IsPicking = false;
        _services.HideMainWindow(false);
    }

    private void OnMoved(int x, int y)
    {
        PickerHit? hit = _picker?.PickAt(x, y, _services.Apps.Inventory);
        Preview = hit;
        _overlay?.Highlight(hit?.Bounds);
        _card?.ShowBeside(_overlay?.Handle ?? 0, (x, y));
        UpdatePassThrough();
    }

    private async void OnPicked(int x, int y)
    {
        PickerHit? hit = _picker?.PickAt(x, y, _services.Apps.Inventory);
        Stop();
        _services.Navigate(Section.Picker);
        await ShowResultAsync(hit);
    }

    private void UpdatePassThrough()
    {
        if (_overlay is null || _card is null)
        {
            return;
        }

        var open = new List<ScreenRect>(_picker?.OverflowButtons ?? []);
        if (_card.ScreenBounds is { } card)
        {
            open.Add(card);
        }

        _overlay.PassThrough(open);
    }

    private async Task ShowResultAsync(PickerHit? hit)
    {
        Result = hit;
        ConfirmingStop = false;
        Notice = null;
        await RefreshActionsAsync();
    }

    private async Task RefreshActionsAsync()
    {
        if (Result is not { } hit)
        {
            Running = [];
            StartupEntries = [];
            return;
        }

        try
        {
            StartupCatalog catalog = _services.Host.CreateStartupCatalog();
            (IReadOnlyList<RunningProcess> running, IReadOnlyList<StartupEntry> startup) = await Task.Run(() => (_actions.Running(hit), PickerActions.StartupEntries(hit, catalog.List())));
            if (Result == hit)
            {
                Running = running;
                StartupEntries = startup;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Error = ex.Message;
        }
    }

    private static bool Uninstallable(InstalledApp? app) =>
        app is { IsProtected: false } && (!app.IsOrphan || app.InstallerKind == InstallerKind.Steam);

    private string Title(PickerHit hit) => hit.WindowTitle.Length > 0 ? hit.WindowTitle : hit.ImagePath is { } image ? Path.GetFileName(image) : S.Untitled;

    private string AppLine(PickerHit hit) => hit.App is { } app ? app.DisplayName ?? app.Id : S.NoInventoryMatch;
}
