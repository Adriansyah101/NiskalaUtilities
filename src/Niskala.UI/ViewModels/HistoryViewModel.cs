using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Guardian.Settings;
using Niskala.Host;
using Niskala.Scanner.Leftovers;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed record RecordRow(string Glyph, string Path, string Status, string Detail, bool IsGood, bool IsBad);

public sealed class TransactionItem(TransactionRecord record)
{
    public TransactionRecord Record { get; } = record;

    public string Id => Record.Id;

    public string Title => Record.AppName ?? Record.AppId;

    public string Kind => Strings.Current.KindOf(Record.Kind);

    public string When => Format.When(Record.StartedAt);

    public string Counts => Strings.Current.TransactionCounts(
        Record.Items.Count(i => i.Status is RemovalStatus.Removed or RemovalStatus.Reverted or RemovalStatus.ScheduledAtReboot or RemovalStatus.Compacted)
            + Record.Settings.Count(s => s.Status is SettingStatus.Applied or SettingStatus.Restored),
        Record.Items.Count + Record.Settings.Count);

    public bool IsRestored => Record.RestoredAt is not null;

    public bool CanRestore => !IsRestored;
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly UiServices _services;
    private readonly TransactionStore _store;
    private bool _stale = true;
    private string? _pendingSelection;

    internal HistoryViewModel(UiServices services)
    {
        _services = services;
        _store = services.Host.CreateTransactionStore();
    }

    public TransactionItemCollection Transactions { get; } = [];

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Records), nameof(RestorePointText), nameof(Method))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial TransactionItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Report { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TextList ReportLines { get; set; } = [];

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsBusy { get; set; }

    public bool HasSelection => Selected is not null;

    public bool HasTransactions => Transactions.Count > 0;

    public string Method => Selected?.Record.Method == RemovalMethod.Permanent ? S.MethodPermanent : S.MethodRecycleBin;

    public string RestorePointText => Selected?.Record.RestorePoint is { } point
        ? point.Created ? S.RestorePointCreated(point.Description) : point.Note ?? S.RestorePointSkipped
        : S.NoRestorePoint;

    public RecordList Records => [.. Selected?.Record.Items.Select(i => new RecordRow(
        i.Kind switch
        {
            LeftoverKind.Folder => "",
            LeftoverKind.File => "",
            LeftoverKind.RegistryKey => "",
            LeftoverKind.RegistryValue => "",
            LeftoverKind.ScheduledTask => "",
            _ => "",
        },
        i.ValueName is null ? i.Path : $"{i.Path} → {(i.ValueName.Length == 0 ? S.DefaultValue : i.ValueName)}",
        S.Status(i.Status),
        i.Detail ?? string.Empty,
        i.Status is RemovalStatus.Removed or RemovalStatus.Reverted or RemovalStatus.Restored,
        i.Status is RemovalStatus.Failed)).Concat(Selected.Record.Settings.Select(s => new RecordRow(
        s.Kind switch
        {
            SettingKind.ServiceStart => "\uE912",
            SettingKind.TaskEnabled => "\uE787",
            _ => "\uE8B7",
        },
        s.Label is { } label ? $"{label} · {s.Target}" : s.Target,
        S.SettingStatusText(s.Status),
        s.Detail ?? string.Empty,
        s.Status is SettingStatus.Applied or SettingStatus.Restored,
        s.Status is SettingStatus.Failed))) ?? []];

    public void Invalidate() => _stale = true;

    public void Select(string? id)
    {
        _pendingSelection = id;
        _stale = true;
    }

    public void EnsureLoaded()
    {
        if (_stale)
        {
            Refresh();
        }
    }

    public void SelectLatestRestorable()
    {
        Refresh();
        Selected = Transactions.FirstOrDefault(t => t.CanRestore) ?? Transactions.FirstOrDefault();
    }

    [RelayCommand]
    public void Refresh()
    {
        _stale = false;
        string? keep = _pendingSelection ?? Selected?.Id;
        _pendingSelection = null;
        Transactions.Clear();
        try
        {
            foreach (TransactionRecord record in _store.List().OrderByDescending(r => r.StartedAt))
            {
                Transactions.Add(new TransactionItem(record));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }

        Selected = Transactions.FirstOrDefault(t => t.Id == keep) ?? Transactions.FirstOrDefault();
        OnPropertyChanged(nameof(HasTransactions));
    }

    private bool CanRestore() => Selected is { CanRestore: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (Selected is not { } item)
        {
            return;
        }

        IsBusy = true;
        Error = string.Empty;
        try
        {
            TransactionRestorer restorer = _services.Host.CreateTransactionRestorer();
            RestoreReport? report = await Task.Run(() => restorer.Restore(item.Id), cancellationToken);
            if (report is null)
            {
                Error = S.TransactionMissing;
                return;
            }

            Report = S.RestoreSummary(report.ItemsRestored, report.ValuesWritten, report.ValuesKept, report.RulesRestored);
            ReportLines = [.. report.Problems, .. report.Notes];
            _pendingSelection = item.Id;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
