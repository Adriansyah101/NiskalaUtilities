using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Niskala.Cleaner.Rules;
using Niskala.Core.Diagnostics;
using Niskala.Host;
using Niskala.Scanner.Icons;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Services;

public enum Section
{
    Apps,
    Tracker,
    Picker,
    QuickScan,
    SelectiveClean,
    Registry,
    Resources,
    Suspend,
    Storage,
    Duplicates,
    Wipe,
    Repair,
    History,
    Updates,
}

public sealed class UiServices : IDisposable
{
    private readonly IconExtractor _extractor = new();
    private CleanRuleCatalog? _catalog;

    public UiServices(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Host = new NiskalaHost(LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new JsonLinesFileLoggerProvider(JsonLinesFileLoggerProvider.DefaultDirectory, "niskala-ui"));
        }));
        Icons = Host.CreateIconCache(_extractor);
        Apps = new AppsViewModel(this);
        Tracker = new TrackerViewModel(this);
        Picker = new TargetPickerViewModel(this);
        QuickScan = new QuickScanViewModel(this);
        SelectiveClean = new SelectiveCleanViewModel(this);
        Integrity = new IntegrityViewModel(this);
        Resources = new ResourcesViewModel(this);
        Suspend = new SuspendViewModel(this);
        Storage = new StorageViewModel(this);
        Duplicates = new DuplicatesViewModel(this);
        Wipe = new WipeViewModel(this);
        Repair = new RepairViewModel(this);
        History = new HistoryViewModel(this);
        Updates = new UpdatesViewModel(this);
        Palette = new PaletteViewModel(this);
        Navigation.CanLeave = section => section == Section.Apps || Apps.Inspector.Leave(FlowExit.Navigate);
        Apps.Inspector.LockChanged += () =>
        {
            Navigation.IsLocked = Apps.Inspector.IsLocked;
            if (Navigation.IsLocked)
            {
                Palette.IsOpen = false;
            }
        };
    }

    public event Action? ThemeToggleRequested;

    public event Action? LanguageToggleRequested;

    public event Action<bool>? MainWindowHidden;

    public DispatcherQueue Dispatcher { get; }

    public NiskalaHost Host { get; }

    public IconCache Icons { get; }

    public AppsViewModel Apps { get; }

    public TrackerViewModel Tracker { get; }

    public TargetPickerViewModel Picker { get; }

    public QuickScanViewModel QuickScan { get; }

    public SelectiveCleanViewModel SelectiveClean { get; }

    public IntegrityViewModel Integrity { get; }

    public ResourcesViewModel Resources { get; }

    public SuspendViewModel Suspend { get; }

    public StorageViewModel Storage { get; }

    public DuplicatesViewModel Duplicates { get; }

    public WipeViewModel Wipe { get; }

    public CleanRuleCatalog Catalog => _catalog ??= Host.LoadRuleCatalog();

    public RepairViewModel Repair { get; }

    public HistoryViewModel History { get; }

    public UpdatesViewModel Updates { get; }

    public PaletteViewModel Palette { get; }

    public NavigationViewModel Navigation { get; } = new();

    public bool IsBusy =>
        Apps.Inspector.IsBusy || Apps.Inspector.ShowScrim || Repair.IsRunning || Tracker.IsRecording || Tracker.IsBusy || Updates.IsDownloading ||
        ((TaskViewModel[])[QuickScan, SelectiveClean, Integrity, Resources, Suspend, Storage, Duplicates, Wipe]).Any(t => t.IsBusy);

    public bool Navigate(Section section) => Navigation.Select(section);

    public void ToggleTheme() => ThemeToggleRequested?.Invoke();

    public void ToggleLanguage() => LanguageToggleRequested?.Invoke();

    public void HideMainWindow(bool hidden) => MainWindowHidden?.Invoke(hidden);

    public void Post(Action action) => Dispatcher.TryEnqueue(() => action());

    public void Relabel()
    {
        Navigation.Relabel(Strings.Current);
        Apps.Relabel();
        Resources.UpdateSummary();
        Updates.Relabel();
    }

    public void Dispose()
    {
        foreach (TaskViewModel page in (TaskViewModel[])[QuickScan, SelectiveClean, Integrity, Resources, Suspend, Storage, Duplicates, Wipe])
        {
            page.Dispose();
        }

        Repair.Dispose();
        Updates.Dispose();
        Apps.Dispose();
        _extractor.Dispose();
        Host.Dispose();
    }
}
