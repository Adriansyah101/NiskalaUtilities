using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Tracker;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Rollback;
using Niskala.Tracker.Tracing;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed record ChangeRow(string Glyph, string Path, string Change);

public sealed class LogItem(InstallLog log)
{
    public InstallLog Log { get; } = log;

    public string Id => Log.Id;

    public string Name => Log.Name;

    public string When => Format.When(Log.StartedAt);

    public string Counts => Strings.Current.LogCounts(Log.Files.Count, Log.Keys.Count, Log.Values.Count);

    public bool IsRolledBack => Log.RolledBackAt is not null;
}

public sealed partial class TrackerViewModel : ObservableObject
{
    private readonly UiServices _services;
    private readonly InstallLogStore _store;
    private CancellationTokenSource? _finish;
    private CancellationTokenSource? _abandon;
    private LogRollback? _rollback;
    private RollbackPlan? _plan;

    internal TrackerViewModel(UiServices services)
    {
        _services = services;
        _store = services.Host.CreateInstallLogStore();
    }

    public ProcessRowCollection Processes { get; } = [];

    public LogItemCollection Logs { get; } = [];

    public LeftoverReviewViewModel Review { get; } = new();

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    public partial string InstallerPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Arguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string Stage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Changes), nameof(Notes), nameof(Summary))]
    public partial LogItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool ShowReview { get; set; }

    [ObservableProperty]
    public partial TextList Kept { get; set; } = [];

    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsRecording;

    public bool HasSelection => Selected is not null;

    public bool HasLogs => Logs.Count > 0;

    public string Summary => Selected?.Log is { } log
        ? S.LogSummary(log.Installer, log.Processes.Count, log.Statistics?.EventsSeen ?? 0, log.Filtered.Values.Sum())
        : string.Empty;

    public TextList Notes => [.. Selected?.Log.Notes ?? []];

    public ChangeList Changes => Selected?.Log is not { } log ? [] :
    [
        .. log.Files.Select(f => new ChangeRow(f.IsDirectory ? "" : "", f.Path, S.Change(f.Change))),
        .. log.Keys.Select(k => new ChangeRow("", k.Path, S.Change(k.Change))),
        .. log.Values.Select(v => new ChangeRow("", $"{v.Key} → {(v.Name.Length == 0 ? S.DefaultValue : v.Name)}", S.Change(v.Change) + (v.RevertOnly ? $" · {S.RevertOnly}" : string.Empty))),
        .. log.FirewallRules.Select(r => new ChangeRow("", r.Name, S.Change(ChangeKind.Created))),
    ];

    public void Refresh()
    {
        string? selected = Selected?.Id;
        Logs.Clear();
        foreach (InstallLog log in _store.List())
        {
            Logs.Add(new LogItem(log));
        }

        Selected = Logs.FirstOrDefault(l => l.Id == selected) ?? Logs.FirstOrDefault();
        OnPropertyChanged(nameof(HasLogs));
    }

    [RelayCommand]
    private void Browse()
    {
        if (Pickers.Installer() is { } file)
        {
            InstallerPath = file;
            if (Name.Length == 0)
            {
                Name = Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    public void BrowseAndShow()
    {
        _services.Navigate(Section.Tracker);
        Browse();
    }

    private bool CanRecord() => !IsRecording && File.Exists(InstallerPath);

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private async Task RecordAsync()
    {
        Error = string.Empty;
        Result = string.Empty;
        Processes.Clear();
        IsRecording = true;
        Stage = S.TrackerStarting;
        _finish = new CancellationTokenSource();
        _abandon = new CancellationTokenSource();
        var request = new TrackRequest(InstallerPath, Arguments.Length == 0 ? null : Arguments, Name.Length == 0 ? Path.GetFileNameWithoutExtension(InstallerPath) : Name);
        var progress = new Progress<TrackProgress>(OnProgress);
        CancellationToken finish = _finish.Token;
        CancellationToken abandon = _abandon.Token;
        try
        {
            InstallTracker tracker = _services.Host.CreateInstallTracker();
            InstallLog log = await Task.Run(() => tracker.RunAsync(request, progress, finish, abandon));
            Result = S.LogSaved(log.Files.Count, log.Keys.Count, log.Values.Count);
            Refresh();
            Selected = Logs.FirstOrDefault(l => l.Id == log.Id);
            await _services.Apps.LoadAsync();
        }
        catch (OperationCanceledException)
        {
            Result = S.RecordingAbandoned;
        }
        catch (TrackerUnavailableException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRecording = false;
            Stage = string.Empty;
            _finish.Dispose();
            _abandon.Dispose();
            _finish = null;
            _abandon = null;
        }
    }

    [RelayCommand]
    private void FinishNow() => TaskViewModel.Stop(_finish);

    [RelayCommand]
    private void Abandon() => TaskViewModel.Stop(_abandon);

    [RelayCommand]
    private async Task PlanRollbackAsync()
    {
        if (Selected?.Log is not { } log)
        {
            return;
        }

        Error = string.Empty;
        Result = string.Empty;
        IsBusy = true;
        try
        {
            _rollback = _services.Host.CreateLogRollback(_services.Host.CreateLeftoverCleaner());
            LogRollback rollback = _rollback;
            _plan = await Task.Run(() => rollback.Plan(log));
            Review.Load(_plan.Items, _ => true);
            Kept = [.. _plan.Warnings, .. _plan.Kept, .. _plan.Reverts.Select(r => S.WillWriteBack(r.Key, r.Previous.Name))];
            ShowReview = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollBackAsync()
    {
        if (_plan is not { } plan || _rollback is not { } rollback)
        {
            return;
        }

        RollbackPlan picked = plan with { Items = Review.Checked };
        var options = new CleanOptions { Permanent = Review.Permanent };
        ShowReview = false;
        IsBusy = true;
        try
        {
            CleanResult result = await Task.Run(() => rollback.ExecuteAsync(picked, options));
            Result = S.CleanSummary(
                Count(result, RemovalStatus.Removed),
                Count(result, RemovalStatus.ScheduledAtReboot),
                Count(result, RemovalStatus.Skipped),
                Count(result, RemovalStatus.Failed),
                Count(result, RemovalStatus.Reverted));
            Refresh();
            _services.History.Invalidate();
            await _services.Apps.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelRollback() => ShowReview = false;

    [RelayCommand]
    private void DeleteLog()
    {
        if (Selected is { } item && _store.Delete(item.Id))
        {
            Logs.Remove(item);
            Selected = Logs.FirstOrDefault();
            OnPropertyChanged(nameof(HasLogs));
        }
    }

    private void OnProgress(TrackProgress progress)
    {
        Stage = S.Recording(progress.Stage);
        if (progress.Process is not { } process)
        {
            return;
        }

        if (Processes.FirstOrDefault(p => p.Id == process.Id) is { } row)
        {
            row.Exited = process.ExitedAt is not null;
            return;
        }

        Processes.Add(new ProcessRow(process.Id, process.ImagePath, process.Adopted) { Exited = process.ExitedAt is not null });
    }

    private static int Count(CleanResult result, RemovalStatus status) => result.Transaction.Items.Count(i => i.Status == status);
}
