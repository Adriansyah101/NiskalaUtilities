using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Startup;
using Niskala.Optimizer.Resources;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed partial class ResourceRow : ObservableObject
{
    private readonly ResourcesViewModel _owner;
    private bool _applying;

    internal ResourceRow(ResourcesViewModel owner, ResourceItem item)
    {
        _owner = owner;
        Item = item;
        _applying = true;
        IsEnabled = item.Enabled;
        _applying = false;
    }

    public ResourceItem Item { get; private set; }

    public string Name => Item.Name;

    public string Command => Item.Command ?? Item.Program ?? Item.Location;

    public string Detail => string.Join(" · ", new[] { Item.Publisher, Strings.Current.ResourceWhere(Item) }.Where(s => !string.IsNullOrEmpty(s)));

    public string Impact => Strings.Current.Impact(Item.Impact);

    public bool IsHeavy => Item.Impact is BootImpact.High or BootImpact.Medium;

    public bool CanToggle => Item.CanToggle;

    public string LockReason => Item.Blocked is null ? string.Empty : Strings.Current.LockedBy(Item.Blocked);

    public bool CanRemove => Item.Kind is ResourceKind.RunEntry or ResourceKind.StartupFolder;

    public string Glyph => Strings.ResourceGlyph(Item.Kind);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _ = SwitchAsync(value);
        }
    }

    private async Task SwitchAsync(bool value)
    {
        ResourceItem? updated = await _owner.SwitchAsync(Item, value);
        _applying = true;
        if (updated is null)
        {
            IsEnabled = !value;
        }
        else
        {
            Item = updated;
        }

        _applying = false;
    }
}

public sealed partial class ResourcesViewModel : TaskViewModel
{
    private static readonly ResourceKind[][] Tabs =
    [
        [ResourceKind.RunEntry, ResourceKind.RunOnceEntry, ResourceKind.StartupFolder],
        [ResourceKind.Service],
        [ResourceKind.ScheduledTask],
        [ResourceKind.ContextMenu],
    ];

    private IReadOnlyList<ResourceItem> _items = [];
    private ResourceControl? _control;

    internal ResourcesViewModel(UiServices services)
        : base(services)
    {
    }

    public ResourceRowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial int Tab { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool IsLoaded => _items.Count > 0;

    partial void OnTabChanged(int value) => Show();

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async _ =>
    {
        Status = S.Reading;
        _control ??= Services.Host.CreateResourceControl();
        ResourceControl control = _control;
        _items = await Task.Run(() => control.List());
        Show();
    });

    [RelayCommand]
    private void OpenLocation(ResourceRow? row)
    {
        if (row?.Item is { } item && (item.Kind == ResourceKind.StartupFolder ? Path.Combine(item.Location, item.ValueName ?? string.Empty) : item.Program) is { } path)
        {
            int error = ShellLauncher.ShowItem(path);
            Error = error == 0 ? null : S.OpenFailed(error);
        }
    }

    [RelayCommand]
    private Task RemoveAsync(ResourceRow? row) => row is null ? Task.CompletedTask : RunAsync(async token =>
    {
        StartupCatalog catalog = Services.Host.CreateStartupCatalog();
        StartupEntry? entry = await Task.Run(() => catalog.List().FirstOrDefault(e =>
            e.Location.Source.Equals(row.Item.Location, StringComparison.OrdinalIgnoreCase)
            && e.Name.Equals(row.Item.ValueName, StringComparison.OrdinalIgnoreCase)), token);
        if (entry is null)
        {
            Info = S.AlreadyGone(row.Name);
            return;
        }

        CleanResult result = await Task.Run(() => catalog.RemoveAsync(entry, token), token);
        Info = result.Transaction.Items.All(i => i.Status == RemovalStatus.Removed)
            ? S.StartupRemoved(row.Name)
            : S.StartupNotRemoved(row.Name, result.Transaction.Items.FirstOrDefault()?.Detail);
        Services.History.Invalidate();
        _items = [.. _items.Where(i => i.Id != row.Item.Id)];
        Show();
    });

    internal async Task<ResourceItem?> SwitchAsync(ResourceItem item, bool enabled)
    {
        Error = null;
        if (_control is not { } control)
        {
            return null;
        }

        ResourceChange? change = await Task.Run(() => control.SetEnabled(item, enabled));
        if (change is not { Succeeded: true })
        {
            Error = S.SwitchFailed(item.Name, change?.Setting.Detail);
            return null;
        }

        ResourceItem updated = item with { Enabled = enabled };
        _items = [.. _items.Select(i => i.Id == item.Id ? updated : i)];
        Info = item.Kind == ResourceKind.ContextMenu ? S.ExplorerRestartNeeded : null;
        Services.History.Invalidate();
        UpdateSummary();
        return updated;
    }

    internal void UpdateSummary()
    {
        ResourceKind[] kinds = Tabs[Math.Clamp(Tab, 0, Tabs.Length - 1)];
        ResourceItem[] shown = [.. _items.Where(i => kinds.Contains(i.Kind))];
        Summary = S.ResourceSummary(shown.Length, shown.Count(i => i.Enabled), shown.Count(i => i.Enabled && i.Impact is BootImpact.High or BootImpact.Medium));
    }

    private void Show()
    {
        ResourceKind[] kinds = Tabs[Math.Clamp(Tab, 0, Tabs.Length - 1)];
        Rows.Clear();
        foreach (ResourceItem item in _items.Where(i => kinds.Contains(i.Kind)))
        {
            Rows.Add(new ResourceRow(this, item));
        }

        UpdateSummary();
        OnPropertyChanged(nameof(IsLoaded));
    }
}
