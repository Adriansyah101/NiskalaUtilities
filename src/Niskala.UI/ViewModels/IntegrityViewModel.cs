using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Cleaner.Registry;
using Niskala.Guardian.Journal;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed partial class IssueRow : ObservableObject
{
    public IssueRow(IntegrityIssue issue)
    {
        Issue = issue;
        IsChecked = issue.Fixable;
    }

    public IntegrityIssue Issue { get; }

    public string Kind => Strings.Current.IssueKind(Issue.Kind);

    public string Glyph => Strings.IssueGlyph(Issue.Kind);

    public string Place => Issue.ValueName is { Length: > 0 } value ? $"{Issue.Path} → {value}" : Issue.Path;

    public string Detail => Issue.Fixable ? Issue.Detail : $"{Issue.Detail} · {Strings.Current.LeftAlone}";

    public bool Fixable => Issue.Fixable;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

public sealed partial class IntegrityViewModel : TaskViewModel
{
    private RegistryIntegrityScanner? _scanner;

    internal IntegrityViewModel(UiServices services)
        : base(services)
    {
    }

    public IssueRowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CreateRestorePoint { get; set; } = true;

    [RelayCommand]
    private Task ScanAsync() => RunAsync(async token =>
    {
        Status = S.Scanning;
        _scanner ??= Services.Host.CreateIntegrityScanner();
        RegistryIntegrityScanner scanner = _scanner;
        IntegrityReport report = await Task.Run(() => scanner.Scan(cancellationToken: token), token);
        Rows.Clear();
        foreach (IntegrityIssue issue in report.Issues)
        {
            Rows.Add(new IssueRow(issue));
        }

        Summary = S.IntegritySummary(report.Issues.Count, report.Fixable.Count);
    });

    [RelayCommand]
    private Task FixAsync() => RunAsync(async token =>
    {
        IntegrityIssue[] chosen = [.. Rows.Where(r => r.IsChecked && r.Fixable).Select(r => r.Issue)];
        if (chosen.Length == 0 || _scanner is not { } scanner)
        {
            Info = S.ScanFirst;
            return;
        }

        Status = CreateRestorePoint ? S.MakingRestorePoint : S.Repairing;
        bool restorePoint = CreateRestorePoint;
        RegistryIntegrityFixer fixer = Services.Host.CreateIntegrityFixer(scanner);
        IntegrityFix fix = await Task.Run(() => fixer.FixAsync(chosen, restorePoint, null, token), token);
        TransactionRecord transaction = fix.Result.Transaction;
        int fixedCount = transaction.Items.Count(i => i.Status == RemovalStatus.Removed);
        int failed = transaction.Items.Count(i => i.Status == RemovalStatus.Failed);
        Info = S.IntegrityFixed(fixedCount, failed, fix.NoLongerBroken.Count, transaction.Id);
        Services.History.Invalidate();
        foreach (IssueRow row in Rows.Where(r => r.IsChecked && r.Fixable).ToList())
        {
            Rows.Remove(row);
        }

        Summary = S.IntegritySummary(Rows.Count, Rows.Count(r => r.Fixable));
    });

    [RelayCommand]
    private void SelectAll() => Check(true);

    [RelayCommand]
    private void SelectNone() => Check(false);

    private void Check(bool value)
    {
        foreach (IssueRow row in Rows.Where(r => r.Fixable))
        {
            row.IsChecked = value;
        }
    }
}
