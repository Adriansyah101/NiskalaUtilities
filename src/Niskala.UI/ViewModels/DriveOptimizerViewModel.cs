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

public sealed partial class DriveRow : ObservableObject
{
    internal DriveRow(DriveVolume volume) => Volume = volume;

    public DriveVolume Volume { get; private set; }

    public string Name => Volume.Name;

    public string Title => string.IsNullOrWhiteSpace(Volume.Label) ? Volume.Name : $"{Volume.Name}  {Volume.Label}";

    public string Media => Strings.Current.MediaName(Volume.Media);

    public bool IsSsd => Volume.Media == MediaKind.Ssd;

    public bool IsHdd => Volume.Media == MediaKind.Hdd;

    public string FileSystem => Volume.FileSystem;

    public string Capacity => Strings.Current.DriveCapacity(Format.Bytes(Volume.TotalBytes), Format.Bytes(Volume.FreeBytes));

    public double UsedPercent => Volume.TotalBytes <= 0 ? 0 : (Volume.TotalBytes - Volume.FreeBytes) * 100.0 / Volume.TotalBytes;

    public string Action => Strings.Current.ActionName(Volume.DefaultAction);

    [ObservableProperty]
    public partial string Fragmentation { get; set; } = Strings.Current.DriveNotAnalyzed;

    internal double? FragmentedPercent { get; set; }

    internal void Update(DriveVolume volume)
    {
        Volume = volume;
        Relabel();
    }

    internal void Relabel()
    {
        Fragmentation = FragmentedPercent is { } percent ? Strings.Current.DriveFragmented(percent) : Strings.Current.DriveNotAnalyzed;
        OnPropertyChanged(string.Empty);
    }
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

public sealed partial class DriveOptimizerViewModel : ObservableObject, IDisposable
{
    public const int MaxFragmentRows = 500;
    public const int TabFragments = 0;
    public const int TabSummary = 1;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MapTick = TimeSpan.FromMilliseconds(250);

    private readonly UiServices _services;
    private readonly DispatcherQueueTimer _clock;
    private readonly DispatcherQueueTimer _mapClock;
    private readonly Lazy<DriveOptimizer> _optimizer = new(NiskalaHost.CreateDriveOptimizer);
    private CancellationTokenSource? _cancel;
    private int _run;
    private bool _moving;
    private DriveMap? _liveMap;
    private long _shownVersion = -1;
    private DateTimeOffset _started;
    private bool _loaded;

    internal DriveOptimizerViewModel(UiServices services)
    {
        _services = services;
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

    public TextList Modes => [S.ModeName(DefragMode.Quick), S.ModeName(DefragMode.Consolidate), S.ModeName(DefragMode.FullZone)];

    public StatusBanner Banner { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHdd), nameof(IsSsd), nameof(ShowModes), nameof(PrimaryText), nameof(SelectedTitle), nameof(SelectedFacts), nameof(HasSelection), nameof(ShowSsdNote), nameof(CanPrimary), nameof(CanDefragCheckedNow))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(OptimizeCommand), nameof(DefragCheckedCommand))]
    public partial DriveRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanPrimary), nameof(CanDefragCheckedNow))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(OptimizeCommand), nameof(DefragCheckedCommand), nameof(CancelCommand), nameof(RefreshCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Elapsed { get; set; } = Clock(TimeSpan.Zero);

    [ObservableProperty]
    public partial string Activity { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFragments), nameof(ShowSummary))]
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

    public string AnalyzeText => AnalyzeLabel(S, IsAnalyzed);

    public bool IsIdle => !IsRunning;

    public bool CanPrimary => CanOptimize();

    public bool CanDefragCheckedNow => CanDefragChecked();

    public bool HasSelection => Selected is not null;

    public bool IsHdd => Selected?.IsHdd == true;

    public bool IsSsd => Selected?.IsSsd == true;

    public bool ShowModes => IsHdd;

    public bool ShowSsdNote => IsSsd;

    public bool ShowMapEmpty => !HasMap;

    public bool ShowFragments => Tab == TabFragments;

    public bool ShowSummary => Tab == TabSummary;

    public DefragMode Mode => (DefragMode)Math.Clamp(ModeIndex, (int)DefragMode.Quick, (int)DefragMode.FullZone);

    public string ModeHint => S.ModeHint(Mode);

    public string PrimaryText => PrimaryLabel(S, Selected?.Volume);

    public string PercentText => FormatPercent(Percent);

    public string SelectedTitle => Selected?.Title ?? string.Empty;

    public string SelectedFacts => Selected is { } row ? $"{S.MediaFacts(row.Volume)} · {row.Capacity}" : string.Empty;

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

    public ClusterState[] Summarize(int cells) => CurrentMap is { } map && cells > 0 ? map.Summarize(cells) : [];

    public void EnsureLoaded()
    {
        if (!_loaded)
        {
            Refresh();
        }
    }

    public void Relabel()
    {
        foreach (DriveRow row in Drives)
        {
            row.Relabel();
        }

        ShowAnalysis();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        _clock.Stop();
        _mapClock.Stop();
        TaskViewModel.Stop(_cancel);
    }

    private DriveMap? CurrentMap => IsRunning && _liveMap is not null ? _liveMap : CurrentAnalysis?.Map;

    private DriveAnalysis? CurrentAnalysis => Selected is { } row ? Cached(row.Volume) : null;

    private DriveAnalysis? Cached(DriveVolume volume) => _optimizer.Value.Cached(volume);

    public static int TabFor(bool wasHdd, bool isHdd, int current) => !isHdd ? TabSummary : wasHdd ? current : TabFragments;

    partial void OnSelectedChanged(DriveRow? oldValue, DriveRow? newValue)
    {
        if (!IsRunning)
        {
            Banner.Clear();
            Percent = 0;
            Activity = string.Empty;
        }

        Tab = TabFor(oldValue?.IsHdd == true, newValue?.IsHdd == true, Tab);
        ShowAnalysis();
    }

    private bool CanRefresh() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        _loaded = true;
        char? keep = Selected?.Volume.Letter;
        IReadOnlyList<DriveVolume> volumes;
        try
        {
            volumes = _optimizer.Value.Volumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner.Show(InfoBarSeverity.Error, ex.Message);
            return;
        }

        Drives.Clear();
        foreach (DriveVolume volume in volumes)
        {
            var row = new DriveRow(volume);
            if (Cached(volume) is { } analysis)
            {
                row.FragmentedPercent = analysis.Summary().FragmentedPercent;
                row.Relabel();
            }

            Drives.Add(row);
        }

        Selected = Drives.FirstOrDefault(d => d.Volume.Letter == keep) ?? Drives.FirstOrDefault();
        if (Drives.Count == 0)
        {
            Banner.Show(InfoBarSeverity.Informational, S.NoDrives);
        }
    }

    private bool CanAnalyze() => !IsRunning && Selected is not null;

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

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        TaskViewModel.Stop(_cancel);
        if (_moving && IsRunning)
        {
            ShowStopping();
        }
    }

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

    private Task DefragmentAsync(DriveRow row, DefragMode mode, IReadOnlyCollection<string>? only) => RunAsync(row, async token =>
    {
        DriveAnalysis analysis = Cached(row.Volume) ?? await AnalyzeCoreAsync(row, token);
        _liveMap = analysis.Map;
        Percent = 0;
        _moving = true;
        DefragResult result = await _optimizer.Value.DefragmentAsync(analysis, mode, only, new Sink(this), token);
        row.FragmentedPercent = analysis.Summary().FragmentedPercent;
        UpdateVolume(row);
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

    private async Task<DriveAnalysis> AnalyzeCoreAsync(DriveRow row, CancellationToken token)
    {
        _liveMap = null;
        HasMap = false;
        MapChanged?.Invoke();
        DriveAnalysis analysis = await _optimizer.Value.AnalyzeAsync(row.Volume, new Sink(this), token);
        row.FragmentedPercent = analysis.Summary().FragmentedPercent;
        row.Relabel();
        ShowAnalysis();
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
        Percent = 0;
        Activity = string.Empty;
        _started = DateTimeOffset.Now;
        Elapsed = Clock(TimeSpan.Zero);
        IsRunning = true;
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
            IsRunning = false;
            RefreshMap(force: true);
        }
    }

    private void UpdateVolume(DriveRow row)
    {
        if (_optimizer.Value.Volumes().FirstOrDefault(v => v.Letter == row.Volume.Letter) is { } fresh)
        {
            row.Update(fresh);
            OnPropertyChanged(nameof(SelectedFacts));
        }
    }

    private void ShowAnalysis()
    {
        Fragments.Clear();
        Summary.Clear();
        HasChecked = false;
        DriveAnalysis? analysis = CurrentAnalysis;
        IsAnalyzed = analysis is not null;
        HasMap = CurrentMap is not null;
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
            new(strings.SummaryFragmentedFiles, Format.Number(summary.FragmentedFiles)),
            new(strings.SummaryExcess, Format.Number(summary.ExcessFragments)),
            new(strings.SummaryShare, Format.Percent(summary.FragmentedPercent)),
            new(strings.SummaryLargestFree, Format.Bytes(summary.LargestFreeClusters * cluster)),
            new(strings.SummaryZones, strings.ZoneSpread(summary.FastFiles, summary.StandardFiles, summary.ArchiveFiles)),
            new(strings.SummarySkipped, strings.SkippedCounts(summary.Protected, summary.Locked, summary.Inaccessible)),
            new(strings.SummaryAnalyzed, Format.When(analyzedAt)),
        ];
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

    private static string Clock(TimeSpan elapsed) => elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private sealed class Sink(DriveOptimizerViewModel owner) : IProgress<DriveProgress>
    {
        private readonly int _run = owner._run;

        public void Report(DriveProgress value) => owner._services.Post(() => owner.Apply(_run, value));
    }
}
