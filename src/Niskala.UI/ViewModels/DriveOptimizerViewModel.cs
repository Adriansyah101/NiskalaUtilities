using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Niskala.Host;
using Niskala.Optimizer.Drives;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public enum ProbeState
{
    Detecting,
    Waiting,
    Failed,
    Ready,
}

public sealed partial class DriveRow : ObservableObject
{
    private readonly Action? _queued;

    internal DriveRow(DriveVolume volume, Action? queued = null)
    {
        Volume = volume;
        _queued = queued;
        Probe = volume.Probed ? ProbeState.Ready : ProbeState.Detecting;
    }

    public DriveVolume Volume { get; private set; }

    public ProbeState Probe { get; private set; }

    public DriveHealth? Health { get; private set; }

    public DriveSummary? Summary { get; private set; }

    public DateTimeOffset? AnalyzedAt { get; private set; }

    public string Name => Volume.Name;

    public string Label => string.IsNullOrWhiteSpace(Volume.Label) ? Strings.Current.NoValue : Volume.Label;

    public string Title => string.IsNullOrWhiteSpace(Volume.Label) ? Volume.Name : $"{Volume.Name}  {Volume.Label}";

    public bool IsDetecting => Probe is ProbeState.Detecting or ProbeState.Waiting;

    public bool CanAnalyze => Probe is ProbeState.Ready or ProbeState.Failed;

    public string Media => Probe == ProbeState.Ready ? Strings.Current.MediaName(Volume.Media) : Strings.Current.NoValue;

    public bool IsSsd => Volume.Media == MediaKind.Ssd;

    public bool IsHdd => Volume.Media == MediaKind.Hdd;

    public string FileSystem => Volume.FileSystem;

    public string Capacity => Strings.Current.DriveCapacity(Format.Bytes(Volume.TotalBytes), Format.Bytes(Volume.FreeBytes));

    public double UsedPercent => Volume.TotalBytes <= 0 ? 0 : (Volume.TotalBytes - Volume.FreeBytes) * 100.0 / Volume.TotalBytes;

    public string Action => Strings.Current.ActionName(Volume.DefaultAction);

    public string QueueName => Strings.Current.QueueCheckName(Volume.Name);

    public bool HasWarning => Health?.Status == HealthStatus.Warning;

    public string Status => StatusText(Strings.Current, IsBusy, Probe, HasWarning, Summary is not null);

    public string LastAnalysis => AnalyzedAt is { } when ? Format.When(when) : Strings.Current.NoValue;

    public string TotalFiles => Summary is { } summary ? Format.Number(summary.Files) : Strings.Current.NoValue;

    public string FragmentedFiles => Summary is { } summary ? Format.Number(summary.FragmentedFiles) : Strings.Current.NoValue;

    public string FreeFragments => Summary is { } summary ? Format.Number(summary.FreeFragments) : Strings.Current.NoValue;

    public string Fragmentation => Summary is { } summary ? Format.Percent(summary.FragmentedPercent) : Strings.Current.NoValue;

    public string Assessment => Summary is { } summary ? Strings.Current.AssessmentName(DriveAssessor.Assess(summary)) : Strings.Current.NoValue;

    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public static string StatusText(Strings strings, bool busy, ProbeState probe, bool warning, bool analyzed)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return busy ? strings.StatusWorking : probe switch
        {
            ProbeState.Detecting => strings.StatusDetecting,
            ProbeState.Waiting => strings.StatusWaiting,
            ProbeState.Failed => strings.StatusNoResponse,
            _ when warning => strings.StatusHealthWarning,
            _ => analyzed ? strings.StatusAnalyzed : strings.DriveNotAnalyzed,
        };
    }

    internal void Update(DriveVolume volume)
    {
        Volume = volume;
        if (volume.Probed)
        {
            Probe = ProbeState.Ready;
        }

        Relabel();
    }

    internal void SetProbe(ProbeState probe)
    {
        Probe = probe;
        Relabel();
    }

    internal void SetHealth(DriveHealth health)
    {
        Health = health;
        Relabel();
    }

    internal void Show(DriveAnalysis? analysis)
    {
        Summary = analysis?.Summary();
        AnalyzedAt = analysis?.AnalyzedAt;
        Relabel();
    }

    internal void Relabel() => OnPropertyChanged(string.Empty);

    partial void OnIsQueuedChanged(bool value) => _queued?.Invoke();

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(Status));
}

public sealed partial class FragmentRow : ObservableObject
{
    private readonly Action _changed;

    internal FragmentRow(DriveFile file, Action changed)
    {
        _changed = changed;
        Path = file.Path;
        Fragments = file.Fragments;
        Size = file.Size;
        Zone = file.Zone;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public int Fragments { get; }

    public long Size { get; }

    public SpeedZone Zone { get; }

    public string FragmentsText => Strings.Current.FragmentCount(Fragments);

    public string SizeText => Format.Bytes(Size);

    public string ZoneText => Strings.Current.ZoneName(Zone);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => _changed();
}

public sealed record SummaryRow(string Label, string Value);

public sealed class StrategyRow
{
    internal StrategyRow(StrategyOption option, Action<StrategyGoal> choose)
    {
        Option = option;
        Apply = new RelayCommand(() => choose(option.Goal), () => option.CanApply);
    }

    public StrategyOption Option { get; }

    public StrategyGoal Goal => Option.Goal;

    public bool IsRecommended => Option.Fit == StrategyFit.Recommended;

    public bool CanApply => Option.CanApply;

    public string Title => Strings.Current.GoalName(Goal);

    public string Hint => Strings.Current.GoalHint(Goal);

    public string FitText => Strings.Current.FitName(Option.Fit);

    public string ModeText => Strings.Current.ModeName(Option.Mode);

    public string ApplyText => Strings.Current.GuideChoose;

    public RelayCommand Apply { get; }
}

public sealed class AttributeRow(AtaAttribute attribute)
{
    public AtaAttribute Attribute { get; } = attribute;

    public string Id => Attribute.Id.ToString("X2", CultureInfo.InvariantCulture);

    public string Name => Strings.Current.AttributeTitle(Attribute.Id);

    public string Current => Format.Number(Attribute.Current);

    public string Worst => Format.Number(Attribute.Worst);

    public string Threshold => Attribute.Threshold is { } threshold ? Format.Number(threshold) : Strings.Current.NoValue;

    public string Raw => Format.Number(Attribute.Raw);

    public bool IsFailing => Attribute.Failing;

    public string State => IsFailing ? Strings.Current.AttributeFailing : Strings.Current.AttributeOk;
}

public sealed class OccupantRow(ClusterOccupant occupant, long bytesPerCluster)
{
    public ClusterOccupant Occupant { get; } = occupant;

    public string Path => Occupant.Path;

    public string Name => System.IO.Path.GetFileName(Occupant.Path);

    public string Folder => System.IO.Path.GetDirectoryName(Occupant.Path) ?? string.Empty;

    public string StateText => Strings.Current.OccupantName(Occupant.State);

    public string SizeText => Format.Bytes(Occupant.Size);

    public string HereText => $"{Strings.Current.InspectorClusters(Occupant.ClustersInRange)} · {Format.Bytes(Occupant.ClustersInRange * Math.Max(1, bytesPerCluster))}";
}

public sealed partial class DriveOptimizerViewModel : ObservableObject, IDisposable
{
    public const int MaxFragmentRows = 500;
    public const int TabMap = 0;
    public const int TabFragments = 1;
    public const int TabSummary = 2;
    public const int NoCell = -1;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MapTick = TimeSpan.FromMilliseconds(250);

    private readonly UiServices _services;
    private readonly DispatcherQueueTimer _clock;
    private readonly DispatcherQueueTimer _mapClock;
    private readonly Lazy<DriveOptimizer> _optimizer = new(NiskalaHost.CreateDriveOptimizer);
    private readonly Lazy<DriveListLoader> _loader;
    private readonly Lazy<MaintenanceScheduler> _scheduler = new(NiskalaHost.CreateMaintenanceScheduler);
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _probe;
    private int _run;
    private int _listAsk;
    private bool _moving;
    private DriveMap? _liveMap;
    private long _shownVersion = -1;
    private DateTimeOffset _started;
    private bool _loaded;
    private int _inspectAsk;
    private (long Start, long End)? _inspected;
    private ClusterInspection? _inspection;
    private ScheduleState _schedule = ScheduleState.Off;
    private bool _scheduleLoaded;
    private bool _scheduleSync;
    private bool _guideCanApply;

    internal DriveOptimizerViewModel(UiServices services)
    {
        _services = services;
        _loader = new Lazy<DriveListLoader>(() => new DriveListLoader(_optimizer.Value));
        _clock = services.Dispatcher.CreateTimer();
        _clock.Interval = Tick;
        _clock.Tick += (_, _) => Elapsed = Clock(DateTimeOffset.Now - _started);
        _mapClock = services.Dispatcher.CreateTimer();
        _mapClock.Interval = MapTick;
        _mapClock.Tick += (_, _) => RefreshMap(force: false);
    }

    public event Action? MapChanged;

    public Strings S => Strings.Current;

    public DriveRowCollection Drives { get; } = [];

    public FragmentRowCollection Fragments { get; } = [];

    public SummaryRowCollection Summary { get; } = [];

    public SummaryRowCollection Health { get; } = [];

    public AttributeRowCollection Attributes { get; } = [];

    public StrategyRowCollection Strategies { get; } = [];

    public OccupantRowCollection Occupants { get; } = [];

    public TextList Modes => [S.ModeName(DefragMode.Quick), S.ModeName(DefragMode.Consolidate), S.ModeName(DefragMode.FullZone)];

    public TextList Schedules => [S.ScheduleName(MaintenanceSchedule.Off), S.ScheduleName(MaintenanceSchedule.Weekly), S.ScheduleName(MaintenanceSchedule.Monthly), S.ScheduleName(MaintenanceSchedule.WhenIdle)];

    public StatusBanner Banner { get; } = new();

    public bool CanSchedule { get; } = ScheduledMaintenance.CanSchedule(Environment.ProcessPath, ScheduledMaintenance.InstalledRoots());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHdd), nameof(IsSsd), nameof(ShowModes), nameof(PrimaryText), nameof(SelectedTitle), nameof(SelectedFacts), nameof(HasSelection), nameof(ShowSsdNote), nameof(CanPrimary), nameof(CanDefragCheckedNow), nameof(CanAnalyzeNow), nameof(CanApplyGuideNow))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(OptimizeCommand), nameof(DefragCheckedCommand), nameof(ApplyGuideCommand))]
    public partial DriveRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanPrimary), nameof(CanDefragCheckedNow), nameof(CanQueueNow), nameof(CanAnalyzeNow), nameof(CanApplyGuideNow))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(OptimizeCommand), nameof(DefragCheckedCommand), nameof(CancelCommand), nameof(RefreshCommand), nameof(AnalyzeQueueCommand), nameof(OptimizeQueueCommand), nameof(ApplyGuideCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Elapsed { get; set; } = Clock(TimeSpan.Zero);

    [ObservableProperty]
    public partial string Activity { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMap), nameof(ShowFragments), nameof(ShowSummary))]
    public partial int Tab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDefragCheckedNow))]
    [NotifyCanExecuteChangedFor(nameof(DefragCheckedCommand))]
    public partial bool HasChecked { get; set; }

    [ObservableProperty]
    public partial string FragmentTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FragmentsNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapEmpty))]
    public partial bool HasMap { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnalyzeText))]
    public partial bool IsAnalyzed { get; set; }

    [ObservableProperty]
    public partial bool IsStopping { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueue), nameof(QueueAnalyzeText), nameof(QueueOptimizeText), nameof(CanQueueNow))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeQueueCommand), nameof(OptimizeQueueCommand))]
    public partial int QueuedCount { get; set; }

    [ObservableProperty]
    public partial bool IsGuideOpen { get; set; }

    [ObservableProperty]
    public partial string GuideVerdict { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeedGoal), nameof(IsImpactGoal), nameof(IsReclaimGoal))]
    public partial StrategyGoal GuideGoal { get; set; }

    [ObservableProperty]
    public partial bool GuideGoalsEnabled { get; set; }

    [ObservableProperty]
    public partial string GuideMainTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GuideMainHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GuideMainFit { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GuideMainMode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool GuideMainRecommended { get; set; }

    [ObservableProperty]
    public partial string HealthNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InspectorTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InspectorNote { get; set; } = Strings.Current.InspectorEmpty;

    [ObservableProperty]
    public partial string InspectorDetail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearInspectionCommand))]
    public partial bool HasInspection { get; set; }

    [ObservableProperty]
    public partial int ScheduleIndex { get; set; }

    [ObservableProperty]
    public partial string ScheduleNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSchedule))]
    public partial bool IsScheduleBusy { get; set; }

    public string AnalyzeText => AnalyzeLabel(S, IsAnalyzed);

    public bool IsIdle => !IsRunning;

    public bool CanPrimary => CanOptimize();

    public bool CanDefragCheckedNow => CanDefragChecked();

    public bool CanQueueNow => CanQueue();

    public bool CanAnalyzeNow => CanAnalyze();

    public bool CanApplyGuideNow => CanApplyGuide();

    public bool CanChangeSchedule => CanSchedule && _scheduleLoaded && !IsScheduleBusy;

    public bool HasSelection => Selected is not null;

    public bool HasQueue => QueuedCount > 0;

    public bool IsHdd => Selected?.IsHdd == true;

    public bool IsSsd => Selected?.IsSsd == true;

    public bool ShowModes => IsHdd;

    public bool ShowSsdNote => IsSsd;

    public bool ShowMapEmpty => !HasMap;

    public bool ShowMap => Tab == TabMap;

    public bool ShowFragments => Tab == TabFragments;

    public bool ShowSummary => Tab == TabSummary;

    public bool IsSpeedGoal
    {
        get => GuideGoal == StrategyGoal.MaximumSpeed;
        set => PickGoal(StrategyGoal.MaximumSpeed, value);
    }

    public bool IsImpactGoal
    {
        get => GuideGoal == StrategyGoal.LowImpact;
        set => PickGoal(StrategyGoal.LowImpact, value);
    }

    public bool IsReclaimGoal
    {
        get => GuideGoal == StrategyGoal.ReclaimSpace;
        set => PickGoal(StrategyGoal.ReclaimSpace, value);
    }

    public string SpeedGoalName => S.GoalName(StrategyGoal.MaximumSpeed);

    public string ImpactGoalName => S.GoalName(StrategyGoal.LowImpact);

    public string ReclaimGoalName => S.GoalName(StrategyGoal.ReclaimSpace);

    public DefragMode Mode => (DefragMode)Math.Clamp(ModeIndex, (int)DefragMode.Quick, (int)DefragMode.FullZone);

    public string ModeHint => S.ModeHint(Mode);

    public string PrimaryText => PrimaryLabel(S, Selected?.Volume);

    public string PercentText => FormatPercent(Percent);

    public string SelectedTitle => Selected?.Title ?? string.Empty;

    public string SelectedFacts => Selected is { } row ? $"{S.MediaFacts(row.Volume)} · {row.Capacity}" : string.Empty;

    public string QueueAnalyzeText => S.QueueAnalyze(QueuedCount);

    public string QueueOptimizeText => S.QueueOptimize(QueuedCount);

    public bool QueueMovesClusters => Drives.Any(d => d.IsQueued && d.Volume.CanRelocate);

    public bool GuideMovesClusters => Selected?.Volume.CanRelocate == true;

    public static string PrimaryLabel(Strings strings, DriveVolume? volume)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return strings.ActionName(volume?.DefaultAction == DriveAction.Trim ? DriveAction.Trim : DriveAction.Defragment);
    }

    public static string AnalyzeLabel(Strings strings, bool analyzed)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return analyzed ? strings.DriveReanalyze : strings.DriveAnalyze;
    }

    public static string FormatPercent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100))}%");

    public static int TabFor(bool isHdd, int current) => current == TabFragments && !isHdd ? TabMap : current;

    public ClusterState[] Summarize(int cells) => CurrentMap is { } map && cells > 0 ? map.Summarize(cells) : [];

    public int HighlightCell(int cells) => !IsRunning && _inspected is { } range && CurrentAnalysis?.Map is { } map && cells > 0 ? map.CellOf(range.Start, cells) : NoCell;

    public void EnsureLoaded()
    {
        if (!_loaded)
        {
            _ = LoadAsync(forgetHealth: false);
            _ = LoadScheduleAsync();
        }
    }

    public void Relabel()
    {
        foreach (DriveRow row in Drives)
        {
            row.Relabel();
        }

        ShowAnalysis();
        ShowSelectedHealth();
        if (_inspection is { } inspection && Selected is { } current)
        {
            ShowInspection(current.Volume, inspection);
        }
        else
        {
            InspectorNote = S.InspectorEmpty;
        }

        _scheduleSync = true;
        OnPropertyChanged(string.Empty);
        _scheduleSync = false;
        ShowSchedule(_schedule);
    }

    public void Dispose()
    {
        _clock.Stop();
        _mapClock.Stop();
        TaskViewModel.Stop(_cancel);
        TaskViewModel.Stop(_probe);
        _probe?.Dispose();
    }

    private DriveMap? CurrentMap => IsRunning && _liveMap is not null ? _liveMap : CurrentAnalysis?.Map;

    private DriveAnalysis? CurrentAnalysis => Selected is { } row ? Cached(row.Volume) : null;

    private DriveAnalysis? Cached(DriveVolume volume) => _optimizer.Value.Cached(volume);

    partial void OnSelectedChanged(DriveRow? oldValue, DriveRow? newValue)
    {
        if (!IsRunning)
        {
            Banner.Clear();
            Percent = 0;
            Activity = string.Empty;
        }

        ResetInspection();
        Tab = TabFor(newValue?.IsHdd == true, Tab);
        ShowAnalysis(resetGoal: true);
        ShowSelectedHealth();
    }

    partial void OnModeIndexChanged(int value)
    {
        GuideGoal = StrategyGuide.GoalFor(Mode);
        ShowGuide(resetGoal: false);
    }

    partial void OnIsGuideOpenChanged(bool value) => ShowGuide(resetGoal: true);

    partial void OnScheduleIndexChanged(int value)
    {
        if (_scheduleSync || !_scheduleLoaded || value < 0 || (MaintenanceSchedule)value == _schedule.Schedule)
        {
            return;
        }

        _ = ApplyScheduleAsync((MaintenanceSchedule)Math.Clamp(value, (int)MaintenanceSchedule.Off, (int)MaintenanceSchedule.WhenIdle));
    }

    private bool CanRefresh() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync(forgetHealth: true);

    private async Task LoadAsync(bool forgetHealth)
    {
        _loaded = true;
        int ask = ++_listAsk;
        TaskViewModel.Stop(_probe);
        _probe?.Dispose();
        var probe = new CancellationTokenSource();
        _probe = probe;
        if (forgetHealth)
        {
            _optimizer.Value.Media.ForgetHealth();
        }

        char? keep = Selected?.Volume.Letter;
        HashSet<char> queued = [.. Drives.Where(d => d.IsQueued).Select(d => d.Volume.Letter)];
        IReadOnlyList<DriveVolume> volumes;
        try
        {
            volumes = await Task.Run(_loader.Value.Shell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner.Show(InfoBarSeverity.Error, ex.Message);
            return;
        }

        if (ask != _listAsk)
        {
            return;
        }

        Drives.Clear();
        foreach (DriveVolume volume in volumes)
        {
            var row = new DriveRow(volume, CountQueued) { IsQueued = queued.Contains(volume.Letter) };
            row.Show(Cached(volume));
            if (_optimizer.Value.Media.TryGetHealth(volume, out DriveHealth health))
            {
                row.SetHealth(health);
            }

            Drives.Add(row);
        }

        CountQueued();
        Selected = Drives.FirstOrDefault(d => d.Volume.Letter == keep) ?? Drives.FirstOrDefault();
        if (Drives.Count == 0)
        {
            Banner.Show(InfoBarSeverity.Informational, S.NoDrives);
        }

        _ = ProbeAsync(volumes, ask, probe.Token);
    }

    private async Task ProbeAsync(IReadOnlyList<DriveVolume> volumes, int ask, CancellationToken token)
    {
        try
        {
            await _loader.Value.ProbeAllAsync(volumes, update => _services.Post(() => ApplyProbe(ask, update)), token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyProbe(int ask, DriveProbeUpdate update)
    {
        if (ask != _listAsk || Drives.FirstOrDefault(d => d.Volume.Letter == update.Volume.Letter) is not { } row)
        {
            return;
        }

        switch (update.Stage)
        {
            case ProbeStage.Slow:
                row.SetProbe(ProbeState.Waiting);
                break;
            case ProbeStage.Failed:
                row.SetProbe(ProbeState.Failed);
                break;
            case ProbeStage.Media:
                row.Update(update.Volume);
                break;
            case ProbeStage.Health when update.Health is { } health:
                row.SetHealth(health);
                break;
        }

        if (ReferenceEquals(row, Selected))
        {
            SelectionUpdated();
        }
    }

    private void SelectionUpdated()
    {
        foreach (string name in (string[])[nameof(IsHdd), nameof(IsSsd), nameof(ShowModes), nameof(PrimaryText), nameof(SelectedTitle), nameof(SelectedFacts), nameof(ShowSsdNote), nameof(CanPrimary), nameof(CanDefragCheckedNow), nameof(CanAnalyzeNow), nameof(CanApplyGuideNow)])
        {
            OnPropertyChanged(name);
        }

        AnalyzeCommand.NotifyCanExecuteChanged();
        OptimizeCommand.NotifyCanExecuteChanged();
        DefragCheckedCommand.NotifyCanExecuteChanged();
        ApplyGuideCommand.NotifyCanExecuteChanged();
        Tab = TabFor(IsHdd, Tab);
        if (!IsRunning)
        {
            FragmentsNote = CurrentAnalysis is null && IsHdd ? S.FragmentsIdle : FragmentsNote;
        }

        ShowGuide(resetGoal: true);
        ShowSelectedHealth();
    }

    private bool CanAnalyze() => !IsRunning && Selected is { CanAnalyze: true };

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => Selected is { } row ? RunAsync(row, async token =>
    {
        DriveAnalysis analysis = await AnalyzeCoreAsync(row, token);
        DriveSummary summary = analysis.Summary();
        return (InfoBarSeverity.Success, S.AnalyzeDone(row.Name, summary.FragmentedPercent, summary.FragmentedFiles, Elapsed));
    }) : Task.CompletedTask;

    private bool CanOptimize() => !IsRunning && Selected is { } row && row.Volume.DefaultAction is DriveAction.Defragment or DriveAction.Trim;

    [RelayCommand(CanExecute = nameof(CanOptimize))]
    private Task OptimizeAsync() => Selected is not { } row ? Task.CompletedTask : row.IsSsd ? TrimAsync(row) : DefragmentAsync(row, Mode, null);

    private bool CanDefragChecked() => !IsRunning && HasChecked && Selected?.Volume.CanRelocate == true;

    [RelayCommand(CanExecute = nameof(CanDefragChecked))]
    private Task DefragCheckedAsync()
    {
        string[] chosen = [.. Fragments.Where(f => f.IsChecked).Select(f => f.Path)];
        return Selected is { } row && chosen.Length > 0 ? DefragmentAsync(row, DefragMode.Quick, chosen) : Task.CompletedTask;
    }

    private bool CanQueue() => !IsRunning && QueuedCount > 0;

    [RelayCommand(CanExecute = nameof(CanQueue))]
    private Task AnalyzeQueueAsync() => RunQueueAsync(QueueKind.Analyze);

    [RelayCommand(CanExecute = nameof(CanQueue))]
    private Task OptimizeQueueAsync() => RunQueueAsync(QueueKind.Optimize);

    private bool CanApplyGuide() => !IsRunning && _guideCanApply && Selected is { } row && (row.IsSsd ? row.Volume.TrimSupported : row.Volume.CanRelocate);

    [RelayCommand(CanExecute = nameof(CanApplyGuide))]
    private Task ApplyGuideAsync()
    {
        if (Selected is not { } row)
        {
            return Task.CompletedTask;
        }

        if (row.IsSsd)
        {
            return TrimAsync(row);
        }

        ApplyStrategy(GuideGoal);
        return DefragmentAsync(row, Mode, null);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        TaskViewModel.Stop(_cancel);
        if (_moving && IsRunning)
        {
            ShowStopping();
        }
    }

    [RelayCommand(CanExecute = nameof(HasInspection))]
    private void ClearInspection()
    {
        ResetInspection();
        RefreshMap(force: true);
    }

    [RelayCommand]
    private void CloseGuide() => IsGuideOpen = false;

    private void ShowStopping()
    {
        if (!IsStopping)
        {
            IsStopping = true;
            Banner.Show(InfoBarSeverity.Informational, S.DriveStopping);
        }
    }

    [RelayCommand]
    private void CheckAll() => SetChecked(true);

    [RelayCommand]
    private void CheckNone() => SetChecked(false);

    public void ApplyStrategy(StrategyGoal goal)
    {
        if (IsRunning || Selected?.Volume.CanRelocate != true)
        {
            return;
        }

        ModeIndex = (int)StrategyGuide.ModeFor(goal);
        GuideGoal = goal;
    }

    public void ChooseGoal(StrategyGoal goal)
    {
        if (IsRunning)
        {
            return;
        }

        GuideGoal = goal;
        ShowGuide(resetGoal: false);
    }

    public async Task InspectAsync(int cell, int cells)
    {
        if (IsRunning || CurrentAnalysis is not { } analysis || cells <= 0 || cell < 0 || cell >= cells)
        {
            return;
        }

        (long start, long end) = analysis.Map.CellRange(cell, cells);
        int ask = ++_inspectAsk;
        _inspected = (start, end);
        _inspection = null;
        Occupants.Clear();
        InspectorTitle = string.Empty;
        InspectorDetail = string.Empty;
        InspectorNote = S.InspectorBusy;
        HasInspection = true;
        Tab = TabMap;
        RefreshMap(force: true);
        ClusterInspection inspection = await Task.Run(() => ClusterInspector.Inspect(analysis, start, end));
        if (ask == _inspectAsk && ReferenceEquals(CurrentAnalysis, analysis))
        {
            ShowInspection(analysis.Volume, inspection);
        }
    }

    private void PickGoal(StrategyGoal goal, bool picked)
    {
        if (picked && GuideGoalsEnabled)
        {
            ChooseGoal(goal);
        }

        OnPropertyChanged(nameof(IsSpeedGoal));
        OnPropertyChanged(nameof(IsImpactGoal));
        OnPropertyChanged(nameof(IsReclaimGoal));
    }

    private Task DefragmentAsync(DriveRow row, DefragMode mode, IReadOnlyCollection<string>? only) => RunAsync(row, async token =>
    {
        DriveAnalysis analysis = Cached(row.Volume) ?? await AnalyzeCoreAsync(row, token);
        _liveMap = analysis.Map;
        Percent = 0;
        _moving = true;
        DefragResult result = await _optimizer.Value.DefragmentAsync(analysis, mode, only, new Sink(this), token);
        UpdateVolume(row);
        row.Show(Cached(row.Volume));
        ShowAnalysis();
        string moved = Format.Bytes(result.ClustersMoved * Math.Max(1, row.Volume.BytesPerCluster));
        InfoBarSeverity severity = result.Cancelled || result.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        return (severity, S.DefragDone(row.Name, result, moved, Clock(result.Elapsed)));
    });

    private Task TrimAsync(DriveRow row) => RunAsync(row, async token =>
    {
        TrimResult result = await _optimizer.Value.TrimAsync(row.Volume, new Sink(this), token);
        UpdateVolume(row);
        InfoBarSeverity severity = result.Outcome switch
        {
            TrimOutcome.Trimmed => InfoBarSeverity.Success,
            TrimOutcome.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Warning,
        };
        return (severity, S.TrimDone(row.Name, result, Clock(result.Elapsed)));
    });

    private Task RunQueueAsync(QueueKind kind)
    {
        DriveRow[] rows = [.. Drives.Where(d => d.IsQueued)];
        if (rows.Length == 0)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<QueueStep> steps = DriveQueue.Plan(rows.Select(r => r.Volume), kind, Mode);
        return RunAsync(rows[0], async token =>
        {
            _moving = kind == QueueKind.Optimize;
            IReadOnlyList<QueueResult> results = await new DriveQueueRunner(_optimizer.Value).RunAsync(steps, new QueueSink(this), token);
            foreach (QueueResult result in results)
            {
                if (Drives.FirstOrDefault(d => d.Volume.Letter == result.Step.Volume.Letter) is not { } row)
                {
                    continue;
                }

                if (result.Defrag is not null || result.Trim is not null)
                {
                    UpdateVolume(row);
                }

                row.Show(Cached(row.Volume));
            }

            ShowAnalysis();
            InfoBarSeverity severity = results.Any(r => r.Outcome is QueueOutcome.Failed or QueueOutcome.Refused) ? InfoBarSeverity.Error
                : results.Any(r => r.Outcome is QueueOutcome.Warning or QueueOutcome.Cancelled or QueueOutcome.Skipped) ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            return (severity, S.QueueDone(results, Elapsed));
        });
    }

    private async Task<DriveAnalysis> AnalyzeCoreAsync(DriveRow row, CancellationToken token)
    {
        _liveMap = null;
        HasMap = false;
        MapChanged?.Invoke();
        DriveAnalysis analysis = await _optimizer.Value.AnalyzeAsync(row.Volume, new Sink(this), token);
        row.Show(analysis);
        ShowAnalysis(resetGoal: true);
        return analysis;
    }

    private async Task RunAsync(DriveRow row, Func<CancellationToken, Task<(InfoBarSeverity Severity, string Message)>> work)
    {
        if (IsRunning)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _run++;
        Banner.Clear();
        ResetInspection();
        Percent = 0;
        Activity = string.Empty;
        _started = DateTimeOffset.Now;
        Elapsed = Clock(TimeSpan.Zero);
        IsRunning = true;
        row.IsBusy = true;
        _clock.Start();
        _mapClock.Start();
        try
        {
            (InfoBarSeverity severity, string message) = await work(cancel.Token);
            if (!cancel.IsCancellationRequested)
            {
                Percent = 100;
            }

            Banner.Show(severity, message);
        }
        catch (OperationCanceledException)
        {
            Banner.Show(InfoBarSeverity.Warning, S.DriveCancelled(row.Name));
        }
        catch (DriveAccessException ex)
        {
            Banner.Show(InfoBarSeverity.Error, ex.NeedsElevation ? S.DriveNeedsAdmin(row.Name) : S.DriveFailed(row.Name, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            UiLog.TaskFailed(_services.Host.LoggerFactory.CreateLogger<DriveOptimizerViewModel>(), ex);
            Banner.Show(InfoBarSeverity.Error, S.DriveFailed(row.Name, ex.Message));
        }
        finally
        {
            _clock.Stop();
            _mapClock.Stop();
            Elapsed = Clock(DateTimeOffset.Now - _started);
            _cancel = null;
            _liveMap = null;
            _moving = false;
            IsStopping = false;
            Activity = string.Empty;
            foreach (DriveRow busy in Drives.Where(d => d.IsBusy))
            {
                busy.IsBusy = false;
            }

            row.IsBusy = false;
            IsRunning = false;
            RefreshMap(force: true);
            ShowGuide(resetGoal: false);
        }
    }

    private void UpdateVolume(DriveRow row)
    {
        if (_optimizer.Value.ListVolumes().FirstOrDefault(v => v.Letter == row.Volume.Letter) is { } fresh)
        {
            row.Update(fresh.Probed ? fresh : fresh with { Media = row.Volume.Media, TrimSupported = row.Volume.TrimSupported, Bus = row.Volume.Bus, RotationRate = row.Volume.RotationRate, Probed = row.Volume.Probed });
            OnPropertyChanged(nameof(SelectedFacts));
        }
    }

    private void ShowAnalysis(bool resetGoal = false)
    {
        Fragments.Clear();
        Summary.Clear();
        HasChecked = false;
        DriveAnalysis? analysis = CurrentAnalysis;
        IsAnalyzed = analysis is not null;
        HasMap = CurrentMap is not null;
        ShowGuide(resetGoal);
        if (analysis is null)
        {
            FragmentTitle = string.Empty;
            FragmentsNote = Selected?.IsHdd == true ? S.FragmentsIdle : string.Empty;
            RefreshMap(force: true);
            return;
        }

        DriveFile[] fragmented = [.. analysis.Fragmented.OrderByDescending(f => f.Fragments).ThenByDescending(f => f.Size)];
        foreach (DriveFile file in fragmented.Take(MaxFragmentRows))
        {
            Fragments.Add(new FragmentRow(file, CountChecked));
        }

        FragmentTitle = fragmented.Length == 0 ? string.Empty : S.FragmentListTitle(Fragments.Count, fragmented.Length);
        FragmentsNote = fragmented.Length == 0 ? S.FragmentsEmpty : string.Empty;
        foreach (SummaryRow row in SummaryRows(S, analysis.Volume, analysis.Summary(), analysis.AnalyzedAt))
        {
            Summary.Add(row);
        }

        RefreshMap(force: true);
    }

    public static IReadOnlyList<SummaryRow> SummaryRows(Strings strings, DriveVolume volume, DriveSummary summary, DateTimeOffset analyzedAt)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(summary);
        long cluster = Math.Max(1, volume.BytesPerCluster);
        double free = summary.TotalClusters == 0 ? 0 : summary.FreeClusters * 100.0 / summary.TotalClusters;
        return
        [
            new(strings.SummaryMedia, strings.MediaFacts(volume)),
            new(strings.SummaryClusters, strings.ClusterCount(summary.TotalClusters, Format.Bytes(cluster))),
            new(strings.SummaryFree, strings.FreeShare(Format.Bytes(summary.FreeClusters * cluster), free)),
            new(strings.SummaryFreeFragments, Format.Number(summary.FreeFragments)),
            new(strings.SummaryFragmentedFiles, Format.Number(summary.FragmentedFiles)),
            new(strings.SummaryExcess, Format.Number(summary.ExcessFragments)),
            new(strings.SummaryShare, Format.Percent(summary.FragmentedPercent)),
            new(strings.ColumnAssessment, strings.AssessmentName(DriveAssessor.Assess(summary))),
            new(strings.SummaryLargestFree, Format.Bytes(summary.LargestFreeClusters * cluster)),
            new(strings.SummaryCompressed, Format.Number(summary.CompressedFiles)),
            new(strings.SummaryReserved, Format.Bytes(summary.ReservedClusters * cluster)),
            new(strings.SummaryZones, strings.ZoneSpread(summary.FastFiles, summary.StandardFiles, summary.ArchiveFiles)),
            new(strings.SummarySkipped, strings.SkippedCounts(summary.Protected, summary.Locked, summary.Inaccessible)),
            new(strings.SummaryAnalyzed, Format.When(analyzedAt)),
        ];
    }

    private void ShowGuide(bool resetGoal)
    {
        Strategies.Clear();
        if (!IsGuideOpen || Selected is not { } row)
        {
            GuideVerdict = string.Empty;
            GuideMainTitle = string.Empty;
            GuideMainHint = string.Empty;
            GuideMainFit = string.Empty;
            GuideMainMode = string.Empty;
            GuideMainRecommended = false;
            GuideGoalsEnabled = false;
            SetGuideApply(false);
            return;
        }

        DriveSummary? summary = CurrentAnalysis?.Summary();
        StrategyAdvice advice = StrategyGuide.Advise(row.Volume, summary);
        GuideVerdict = S.GuideVerdict(advice, row.Volume, summary);
        if (advice.UsesTrim)
        {
            GuideGoalsEnabled = false;
            GuideMainTitle = S.DriveTrim;
            GuideMainHint = S.GuideTrimHint;
            GuideMainFit = S.FitName(StrategyFit.Recommended);
            GuideMainMode = string.Empty;
            GuideMainRecommended = true;
            foreach (StrategyOption option in advice.Options)
            {
                Strategies.Add(new StrategyRow(option, ChooseGoal));
            }

            SetGuideApply(row.Volume.TrimSupported);
            return;
        }

        if (resetGoal)
        {
            GuideGoal = advice.Recommended ?? StrategyGuide.GoalFor(Mode);
        }

        StrategyOption main = advice.Option(GuideGoal);
        GuideGoalsEnabled = advice.AllowsRelocation;
        GuideMainTitle = S.GoalName(main.Goal);
        GuideMainHint = main.CanApply ? S.GoalHint(main.Goal) : S.GuideNoAction;
        GuideMainFit = S.FitName(main.Fit);
        GuideMainMode = main.CanApply ? S.ModeName(main.Mode) : string.Empty;
        GuideMainRecommended = main.Fit == StrategyFit.Recommended;
        foreach (StrategyOption option in advice.Options.Where(o => o.Goal != main.Goal))
        {
            Strategies.Add(new StrategyRow(option, ChooseGoal));
        }

        SetGuideApply(main.CanApply && row.Volume.CanRelocate);
    }

    private void SetGuideApply(bool value)
    {
        _guideCanApply = value;
        OnPropertyChanged(nameof(CanApplyGuideNow));
        ApplyGuideCommand.NotifyCanExecuteChanged();
    }

    private void ShowSelectedHealth()
    {
        Health.Clear();
        Attributes.Clear();
        if (Selected is not { } row)
        {
            HealthNote = string.Empty;
            return;
        }

        if (row.Health is not { } health)
        {
            HealthNote = row.Probe == ProbeState.Failed ? S.HealthUnavailable(row.Volume.Bus is StorageBus.Usb or StorageBus.Ieee1394) : S.HealthReading;
            return;
        }

        ShowHealth(row.Volume, health);
    }

    private void ShowHealth(DriveVolume volume, DriveHealth health)
    {
        Health.Clear();
        foreach (SummaryRow row in HealthRows(S, volume, health))
        {
            Health.Add(row);
        }

        Attributes.Clear();
        foreach (AtaAttribute attribute in health.Table)
        {
            Attributes.Add(new AttributeRow(attribute));
        }

        HealthNote = health.HasData ? string.Empty : S.HealthUnavailable(health.Bridged);
    }

    public static IReadOnlyList<SummaryRow> HealthRows(Strings strings, DriveVolume volume, DriveHealth health)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(health);
        if (!health.HasData)
        {
            return [];
        }

        var rows = new List<SummaryRow> { new(strings.HealthStatusLabel, strings.HealthStatusName(health.Status)) };
        if (health.Warnings != HealthWarning.None)
        {
            rows.Add(new(strings.HealthIssues, strings.HealthWarnings(health.Warnings)));
        }

        if (health.TemperatureCelsius is { } temperature)
        {
            rows.Add(new(strings.HealthTemperature, strings.Celsius(temperature)));
        }

        if (health.PowerOnHours is { } hours)
        {
            rows.Add(new(strings.HealthHours, strings.PowerOnHours(hours)));
        }

        if (volume.Media == MediaKind.Ssd && health.LifeUsedPercent is { } used && health.LifeRemainingPercent is { } remaining)
        {
            rows.Add(new(strings.HealthWear, strings.WearText(used, remaining)));
        }

        if (health.AvailableSparePercent is { } spare)
        {
            rows.Add(new(strings.HealthSpare, Format.Percent(spare)));
        }

        if (health.ReallocatedSectors is not null || health.PendingSectors is not null || health.UncorrectableSectors is not null)
        {
            rows.Add(new(strings.HealthSectors, strings.SectorCounts(health)));
        }

        if (health.Counters is { } counters)
        {
            rows.Add(new(strings.HealthDataRead, Format.Bytes(Scaled(counters.DataUnitsRead))));
            rows.Add(new(strings.HealthDataWritten, Format.Bytes(Scaled(counters.DataUnitsWritten))));
            rows.Add(new(strings.HealthPowerCycles, Format.Number(counters.PowerCycles)));
            rows.Add(new(strings.HealthUnsafeShutdowns, Format.Number(counters.UnsafeShutdowns)));
            rows.Add(new(strings.HealthMediaErrors, Format.Number(counters.MediaErrors)));
        }

        if (health.Protocol != HealthProtocol.Unknown || health.Bridged)
        {
            rows.Add(new(strings.HealthInterface, strings.InterfaceText(health)));
        }

        if (health.Model is { Length: > 0 } model)
        {
            rows.Add(new(strings.HealthModel, model));
        }

        rows.Add(new(strings.HealthSourceLabel, strings.HealthSourceName(health.Source)));
        return rows;

        static long Scaled(long units) => units > long.MaxValue / NvmeCounters.BytesPerDataUnit ? long.MaxValue : units * NvmeCounters.BytesPerDataUnit;
    }

    private void ShowInspection(DriveVolume volume, ClusterInspection inspection)
    {
        _inspection = inspection;
        Occupants.Clear();
        foreach (ClusterOccupant occupant in inspection.Files)
        {
            Occupants.Add(new OccupantRow(occupant, volume.BytesPerCluster));
        }

        InspectorTitle = S.InspectorTitle(inspection, volume.BytesPerCluster);
        long cluster = Math.Max(1, volume.BytesPerCluster);
        var details = new List<string>();
        if (inspection.Files.Count < inspection.TotalFiles)
        {
            details.Add(S.InspectorMore(inspection.Files.Count, inspection.TotalFiles));
        }

        if (inspection.ReservedClusters > 0)
        {
            details.Add(S.InspectorReserved(inspection.ReservedClusters * cluster));
        }

        if (inspection.UnlistedClusters > 0)
        {
            details.Add(S.InspectorUnlisted(inspection.UnlistedClusters * cluster));
        }

        InspectorDetail = string.Join(" ", details);
        InspectorNote = inspection.TotalFiles == 0 ? S.InspectorNoFiles : string.Empty;
    }

    private void ResetInspection()
    {
        _inspectAsk++;
        _inspected = null;
        _inspection = null;
        Occupants.Clear();
        InspectorTitle = string.Empty;
        InspectorDetail = string.Empty;
        InspectorNote = S.InspectorEmpty;
        HasInspection = false;
    }

    private async Task LoadScheduleAsync()
    {
        IsScheduleBusy = true;
        ScheduleNote = S.ScheduleReading;
        ScheduleState state = await Task.Run(_scheduler.Value.Read);
        _scheduleLoaded = true;
        ShowSchedule(state);
        IsScheduleBusy = false;
    }

    private async Task ApplyScheduleAsync(MaintenanceSchedule schedule)
    {
        if (!CanChangeSchedule || Environment.ProcessPath is not { } program)
        {
            ShowSchedule(_schedule);
            return;
        }

        IsScheduleBusy = true;
        ScheduleState state = await Task.Run(() => _scheduler.Value.Apply(schedule, program));
        ShowSchedule(state);
        IsScheduleBusy = false;
    }

    private void ShowSchedule(ScheduleState state)
    {
        _schedule = state;
        _scheduleSync = true;
        ScheduleIndex = (int)state.Schedule;
        _scheduleSync = false;
        if (_scheduleLoaded)
        {
            ScheduleNote = CanSchedule ? S.ScheduleNote(state) : $"{S.ScheduleNote(state)} {S.ScheduleNeedsInstall}";
        }

        OnPropertyChanged(nameof(CanChangeSchedule));
    }

    private void RefreshMap(bool force)
    {
        long version = CurrentMap?.Version ?? -1;
        HasMap = CurrentMap is not null;
        if (force || version != _shownVersion)
        {
            _shownVersion = version;
            MapChanged?.Invoke();
        }
    }

    private void SetChecked(bool value)
    {
        foreach (FragmentRow row in Fragments)
        {
            row.IsChecked = value;
        }

        CountChecked();
    }

    private void CountChecked() => HasChecked = Fragments.Any(f => f.IsChecked);

    private void CountQueued() => QueuedCount = Drives.Count(d => d.IsQueued);

    public static bool IsCurrent(int run, int current, bool running) => running && run == current;

    private void Apply(int run, DriveProgress progress)
    {
        if (!IsCurrent(run, _run, IsRunning))
        {
            return;
        }

        Percent = progress.Percent;
        Activity = S.DrivePhaseText(progress.Phase, progress.Current);
        if (progress.Phase == DrivePhase.Stopping)
        {
            ShowStopping();
        }

        if (progress.Map is { } map)
        {
            _liveMap = map;
        }
    }

    private void ApplyQueue(int run, QueueProgress progress)
    {
        if (!IsCurrent(run, _run, IsRunning))
        {
            return;
        }

        if (Drives.FirstOrDefault(d => d.Volume.Letter == progress.Step.Volume.Letter) is { } row)
        {
            foreach (DriveRow other in Drives.Where(d => d.IsBusy && !ReferenceEquals(d, row)))
            {
                other.IsBusy = false;
            }

            row.IsBusy = true;
            if (!ReferenceEquals(Selected, row))
            {
                _liveMap = null;
                Selected = row;
            }
        }

        Apply(run, progress.Drive);
        Activity = S.QueueActivity(progress.Index, progress.Count, Activity);
    }

    private static string Clock(TimeSpan elapsed) => elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private sealed class Sink(DriveOptimizerViewModel owner) : IProgress<DriveProgress>
    {
        private readonly int _run = owner._run;

        public void Report(DriveProgress value) => owner._services.Post(() => owner.Apply(_run, value));
    }

    private sealed class QueueSink(DriveOptimizerViewModel owner) : IProgress<QueueProgress>
    {
        private readonly int _run = owner._run;

        public void Report(QueueProgress value) => owner._services.Post(() => owner.ApplyQueue(_run, value));
    }
}
