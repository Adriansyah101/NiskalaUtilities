using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Cleaner;
using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Host;
using Niskala.Optimizer.Resources;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed partial class CleanRuleRow : ObservableObject
{
    public CleanRuleRow(CleanRule rule, bool isChecked)
    {
        Rule = rule;
        IsChecked = isChecked;
    }

    public CleanRule Rule { get; }

    public string Name => Rule.DisplayName(Strings.Current.Indonesian);

    public string Description => Rule.Description?.For(Strings.Current.Indonesian) ?? string.Empty;

    public string Traits => Strings.Current.RuleTraits(Rule);

    public string Glyph => Strings.CategoryGlyph(Rule.Category);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsReady))]
    public partial RulePreview? Preview { get; set; }

    public string Summary => Preview is null ? Strings.Current.NotScanned : Strings.Current.PreviewSummary(Preview);

    public bool IsReady => Preview?.State == PreviewState.Ready;
}

public sealed partial class HindranceRow : ObservableObject
{
    private readonly QuickScanViewModel _owner;

    internal HindranceRow(QuickScanViewModel owner, StartupHindrance hindrance)
    {
        _owner = owner;
        Hindrance = hindrance;
    }

    public StartupHindrance Hindrance { get; }

    public string Name => Hindrance.Name;

    public string Detail => string.Join(" · ", new[] { Strings.Current.Impact(Hindrance.Impact), Hindrance.Publisher, Hindrance.Source }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitchOff))]
    public partial bool IsOff { get; set; }

    public bool CanSwitchOff => !IsOff;

    [RelayCommand]
    private Task SwitchOffAsync() => _owner.SwitchOffAsync(this);
}

public sealed partial class QuickScanViewModel : TaskViewModel
{
    internal QuickScanViewModel(UiServices services)
        : base(services)
    {
    }

    public CleanRuleRowCollection Rows { get; } = [];

    public HindranceRowCollection Startup { get; } = [];

    [ObservableProperty]
    public partial string SystemTotal { get; set; } = "—";

    [ObservableProperty]
    public partial string BrowserTotal { get; set; } = "—";

    [ObservableProperty]
    public partial string StartupTotal { get; set; } = "—";

    [ObservableProperty]
    public partial string Locked { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    public partial bool HasResults { get; set; }

    [ObservableProperty]
    public partial bool CreateRestorePoint { get; set; } = true;

    public bool CanClean => HasResults && Rows.Any(r => r.IsReady);

    [RelayCommand]
    private Task ScanAsync() => RunAsync(async token =>
    {
        Status = S.Scanning;
        CleanRuleCatalog catalog = Services.Catalog;
        QuickScanReport report = await Task.Run(() => Services.Host.CreateQuickScanner(catalog).Scan(cancellationToken: token), token);
        Rows.Clear();
        foreach (RulePreview preview in report.System.Concat(report.Browsers))
        {
            Rows.Add(new CleanRuleRow(preview.Rule, preview.State == PreviewState.Ready) { Preview = preview });
        }

        Startup.Clear();
        foreach (StartupHindrance hindrance in report.Startup)
        {
            Startup.Add(new HindranceRow(this, hindrance));
        }

        SystemTotal = Format.Bytes(report.SystemBytes);
        BrowserTotal = Format.Bytes(report.BrowserBytes);
        StartupTotal = Format.Number(report.Startup.Count);
        Locked = (report.LockedBrowsers.Count > 0 ? S.CloseBrowsers(report.LockedBrowsers) : null) ?? string.Empty;
        HasResults = true;
        OnPropertyChanged(nameof(CanClean));
    });

    [RelayCommand]
    private Task CleanAsync() => RunAsync(async token =>
    {
        RulePreview[] chosen = [.. Rows.Where(r => r.IsChecked && r.IsReady).Select(r => r.Preview!)];
        CleanRun run = await CleanFlow.RunAsync(Services, chosen, "quick-scan", S.NavQuickScan, permanent: false, CreateRestorePoint, status => Post(() => Status = status), token);
        Info = S.CleanFinished(run);
        Services.History.Invalidate();
        HasResults = false;
        Rows.Clear();
    });

    internal async Task SwitchOffAsync(HindranceRow row)
    {
        Error = string.Empty;
        ResourceControl control = Services.Host.CreateResourceControl();
        ResourceChange? change = await Task.Run(() => control.List().FirstOrDefault(i => i.Id == row.Hindrance.Id) is { } item ? control.SetEnabled(item, enabled: false) : null);
        if (change is { Succeeded: true })
        {
            row.IsOff = true;
            Services.History.Invalidate();
        }
        else
        {
            Error = S.SwitchFailed(row.Name, change?.Setting.Detail);
        }
    }
}

public sealed partial class SelectiveCleanViewModel : TaskViewModel
{
    internal SelectiveCleanViewModel(UiServices services)
        : base(services)
    {
    }

    public CleanRuleRowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial bool Permanent { get; set; }

    [ObservableProperty]
    public partial bool CreateRestorePoint { get; set; } = true;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public void EnsureLoaded()
    {
        if (Rows.Count > 0)
        {
            return;
        }

        foreach (CleanRule rule in Services.Catalog.Rules.OrderBy(r => r.Group).ThenBy(r => r.Browser?.Name).ThenBy(r => r.Category))
        {
            Rows.Add(new CleanRuleRow(rule, rule.Quick));
        }

        Summary = S.RulesSummary(Rows.Count, Services.Catalog.Problems.Count);
    }

    [RelayCommand]
    private void SelectQuick()
    {
        foreach (CleanRuleRow row in Rows)
        {
            row.IsChecked = row.Rule.Quick;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (CleanRuleRow row in Rows)
        {
            row.IsChecked = false;
        }
    }

    [RelayCommand]
    private Task ScanAsync() => RunAsync(async token =>
    {
        Status = S.Scanning;
        CleanRuleRow[] chosen = [.. Rows.Where(r => r.IsChecked)];
        IReadOnlyList<RulePreview> previews = await Task.Run(() => NiskalaHost.CreateCleanScanner().Preview([.. chosen.Select(r => r.Rule)], cancellationToken: token), token);
        for (int i = 0; i < chosen.Length; i++)
        {
            chosen[i].Preview = previews[i];
        }

        Summary = S.SelectionSummary(previews.Count(p => p.State == PreviewState.Ready), previews.Where(p => p.State == PreviewState.Ready).Sum(p => p.Bytes));
    });

    [RelayCommand]
    private Task CleanAsync() => RunAsync(async token =>
    {
        RulePreview[] chosen = [.. Rows.Where(r => r.IsChecked && r.IsReady).Select(r => r.Preview!)];
        if (chosen.Length == 0)
        {
            Info = S.ScanFirst;
            return;
        }

        CleanRun run = await CleanFlow.RunAsync(Services, chosen, "selective-clean", S.NavSelectiveClean, Permanent, CreateRestorePoint, status => Post(() => Status = status), token);
        Info = S.CleanFinished(run);
        Services.History.Invalidate();
        foreach (CleanRuleRow row in Rows)
        {
            row.Preview = null;
        }
    });
}

internal static class CleanFlow
{
    public static Task<CleanRun> RunAsync(
        UiServices services,
        IReadOnlyList<RulePreview> previews,
        string subjectId,
        string subjectName,
        bool permanent,
        bool restorePoint,
        Action<string> status,
        CancellationToken token)
    {
        var options = new CleanRunOptions { SubjectId = subjectId, SubjectName = subjectName, Permanent = permanent, CreateRestorePoint = restorePoint };
        var progress = new Progress<CleanRunProgress>(p => status(Strings.Current.Cleaning(p.RuleId, p.Done, p.Total)));
        status(restorePoint ? Strings.Current.MakingRestorePoint : Strings.Current.Cleaning(string.Empty, 0, 0));
        return Task.Run(() => services.Host.CreateSystemCleaner().CleanAsync(previews, options, progress, token), token);
    }
}
