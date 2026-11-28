using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Niskala.Core.Models;
using Niskala.Scanner.Icons;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Sizing;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public enum AppFilter
{
    All,
    Win32,
    Store,
    Large,
    Recent,
    Broken,
}

public enum AppSort
{
    Name,
    Size,
    Date,
}

public sealed partial class AppsViewModel : ObservableObject, IDisposable
{
    private const int IconDecodeSize = 96;

    private readonly UiServices _services;
    private readonly Dictionary<string, long> _measured = new(StringComparer.OrdinalIgnoreCase);
    private readonly SizeQueue _sizes;
    private readonly IconScheduler _icons;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private List<AppItem> _items = [];
    private bool _disposed;

    internal AppsViewModel(UiServices services)
    {
        _services = services;
        _sizes = services.Host.CreateSizeQueue();
        _icons = new IconScheduler((app, token) => Task.Run(() => services.Icons.GetAsync(app, token), token));
        Inspector = new InspectorViewModel(services);
        _sizes.Measured += size => services.Post(() => OnMeasured(size));
    }

    public InspectorViewModel Inspector { get; }

    public IReadOnlyList<InstalledApp> Inventory { get; private set; } = [];

    public IReadOnlyList<AppItem> Items => _items;

    [ObservableProperty]
    public partial AppItemCollection Visible { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsWin32), nameof(IsStore), nameof(IsLarge), nameof(IsRecent), nameof(IsBroken))]
    public partial AppFilter Filter { get; set; }

    [ObservableProperty]
    public partial AppSort Sort { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int CountAll { get; set; }

    [ObservableProperty]
    public partial int CountWin32 { get; set; }

    [ObservableProperty]
    public partial int CountStore { get; set; }

    [ObservableProperty]
    public partial int CountLarge { get; set; }

    [ObservableProperty]
    public partial int CountRecent { get; set; }

    [ObservableProperty]
    public partial int CountBroken { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBatch), nameof(HasChecked), nameof(BatchLabel))]
    [NotifyCanExecuteChangedFor(nameof(BatchRemoveCommand))]
    public partial int CheckedCount { get; set; }

    public bool CanBatch => CheckedCount >= 2;

    public bool HasChecked => CheckedCount > 0;

    public string BatchLabel => S.BatchRemove(CheckedCount);

    public bool IsAll => Filter == AppFilter.All;

    public bool IsWin32 => Filter == AppFilter.Win32;

    public bool IsStore => Filter == AppFilter.Store;

    public bool IsLarge => Filter == AppFilter.Large;

    public bool IsRecent => Filter == AppFilter.Recent;

    public bool IsBroken => Filter == AppFilter.Broken;

    public bool IsEmpty => !IsLoading && Visible.Count == 0;

    public Strings S => Strings.Current;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        Error = string.Empty;
        try
        {
            InventorySnapshot snapshot = await Task.Run(() => _services.Host.CreateInventoryCollector().CollectAsync());
            if (_disposed)
            {
                return;
            }

            Apply(snapshot.Apps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Apply(IReadOnlyList<InstalledApp> inventory)
    {
        Inventory = inventory;
        Dictionary<string, AppItem> previous = _items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        foreach (AppItem old in _items)
        {
            old.PropertyChanged -= OnItemChanged;
        }

        _checked.RemoveWhere(id => !inventory.Any(a => a.IsVisible && a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        _items = [.. inventory.Where(a => a.IsVisible).Select(a =>
        {
            var item = new AppItem(a) { IsChecked = _checked.Contains(a.Id) };
            item.PropertyChanged += OnItemChanged;
            if (previous.TryGetValue(a.Id, out AppItem? old))
            {
                item.Icon = old.Icon;
                item.IconRequested = old.Icon is not null;
            }

            if (_measured.TryGetValue(a.Id, out long bytes))
            {
                item.MeasuredBytes = bytes;
            }

            return item;
        })];

        List<AppItem> pending = [.. _items.Where(i => i.MeasuredBytes is null && i.MeasurableFolder is not null)];
        foreach (AppItem item in pending)
        {
            item.SizePending = true;
        }

        _ = Task.Run(() =>
        {
            foreach (AppItem item in pending)
            {
                if (_disposed)
                {
                    return;
                }

                _sizes.Enqueue(new SizeRequest(item.Id, item.MeasurableFolder!));
            }
        });

        Recount();
        Refilter();
    }

    public void Realized(AppItem item)
    {
        if (item.SizePending)
        {
            _sizes.Prioritize(item.Id);
        }

        if (!item.IconRequested)
        {
            item.IconRequested = true;
            _ = LoadIconAsync(item);
        }
    }

    public void Recycled(AppItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Icon is null && _icons.Withdraw(item.Id))
        {
            item.IconRequested = false;
        }
    }

    public void SelectFilter(AppFilter filter)
    {
        Filter = filter;
        OnPropertyChanged(nameof(Filter));
        Refilter();
    }

    public void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppItem item)
        {
            Inspector.Show(item);
        }
    }

    public void Relabel()
    {
        Recount();
        OnPropertyChanged(nameof(BatchLabel));
    }

    [RelayCommand(CanExecute = nameof(CanBatch))]
    private async Task BatchRemoveAsync()
    {
        List<InstalledApp> apps = [.. _items.Where(i => i.IsChecked && i.CanBatch).Select(i => i.App)];
        ClearChecks();
        await Inspector.StartBatchAsync(apps);
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (AppItem item in _items.Where(i => i.IsChecked))
        {
            item.IsChecked = false;
        }
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppItem.IsChecked) || sender is not AppItem item)
        {
            return;
        }

        if (item.IsChecked && item.CanBatch)
        {
            _checked.Add(item.Id);
        }
        else
        {
            _checked.Remove(item.Id);
            item.IsChecked = false;
        }

        CheckedCount = _checked.Count;
    }

    public AppItem? Find(string id) => _items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void Open(InstalledApp app, FlowKind start)
    {
        AppItem item = Find(app.Id) ?? new AppItem(app);
        _services.Navigate(Section.Apps);
        Inspector.Show(item, inInventory: Find(app.Id) is not null);
        IRelayCommand? command = start switch
        {
            FlowKind.Uninstall or FlowKind.Steam => Inspector.UninstallCommand,
            FlowKind.Force => Inspector.ForceCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icons.Dispose();
        _sizes.Dispose();
        foreach (AppItem item in _items)
        {
            item.PropertyChanged -= OnItemChanged;
        }
    }

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnSortChanged(AppSort value) => Refilter();

    private void Refilter()
    {
        string query = Query.Trim();
        IEnumerable<AppItem> items = _items.Where(i => Filter switch
        {
            AppFilter.Win32 => i.IsWin32,
            AppFilter.Store => i.IsStore,
            AppFilter.Large => i.IsLarge,
            AppFilter.Recent => i.IsRecent,
            AppFilter.Broken => i.IsBroken,
            _ => true,
        });

        if (query.Length > 0)
        {
            items = items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (i.App.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        items = Sort switch
        {
            AppSort.Size => items.OrderByDescending(i => i.SizeForSort ?? -1),
            AppSort.Date => items.OrderByDescending(i => i.App.InstallDate ?? DateOnly.MinValue),
            _ => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Visible = [.. items];
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Recount()
    {
        CountAll = _items.Count;
        CountWin32 = _items.Count(i => i.IsWin32);
        CountStore = _items.Count(i => i.IsStore);
        CountLarge = _items.Count(i => i.IsLarge);
        CountRecent = _items.Count(i => i.IsRecent);
        CountBroken = _items.Count(i => i.IsBroken);
        long total = _items.Sum(i => i.SizeForSort ?? 0);
        Summary = S.AppsSummary(CountAll, Format.Bytes(total));
    }

    private void OnMeasured(FolderSize size)
    {
        if (size.Exists && size.Error is null)
        {
            _measured[size.Id] = size.Bytes;
        }

        if (Find(size.Id) is { } item)
        {
            item.SizePending = false;
            if (size.Exists && size.Error is null)
            {
                item.MeasuredBytes = size.Bytes;
            }

            Recount();
        }
    }

    private async Task LoadIconAsync(AppItem item)
    {
        string? file;
        try
        {
            file = await _icons.Request(item.App);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UiLog.IconLoadFailed(_services.Host.LoggerFactory.CreateLogger<AppsViewModel>(), item.App.Id, ex);
            return;
        }

        if (file is not null && !_disposed)
        {
            item.Icon = new BitmapImage(new Uri(file)) { DecodePixelWidth = IconDecodeSize, DecodePixelType = DecodePixelType.Logical };
        }
    }
}
