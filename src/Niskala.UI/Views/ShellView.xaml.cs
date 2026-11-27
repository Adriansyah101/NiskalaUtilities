using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;
using Windows.System;

namespace Niskala.UI.Views;

public sealed partial class ShellView : UserControl
{
    private const double PaletteTop = 56;

    private readonly UiServices _services;
    private readonly Dictionary<Section, UIElement> _pages = [];

    public ShellView(UiServices services)
    {
        _services = services;
        InitializeComponent();
        ThemeItem.Content = Application.Current is App { Theme: ElementTheme.Light } ? S.ThemeDark : S.ThemeLight;
        services.SectionRequested += Select;
        Unloaded += (_, _) => services.SectionRequested -= Select;
        Navigation.SelectedItem = AppsItem;
    }

    public Strings S => Strings.Current;

    public PaletteViewModel Palette => _services.Palette;

    public UpdatesViewModel Updates => _services.Updates;

    private void Select(Section section) => Navigation.SelectedItem = section switch
    {
        Section.Tracker => TrackerItem,
        Section.Picker => PickerItem,
        Section.QuickScan => QuickScanItem,
        Section.SelectiveClean => SelectiveCleanItem,
        Section.Registry => RegistryItem,
        Section.Resources => ResourcesItem,
        Section.Suspend => SuspendItem,
        Section.Storage => StorageItem,
        Section.Duplicates => DuplicatesItem,
        Section.Wipe => WipeItem,
        Section.Repair => RepairItem,
        Section.History => HistoryItem,
        Section.Updates => UpdatesItem,
        _ => AppsItem,
    };

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse(tag, out Section section))
        {
            Show(section);
        }
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "Theme":
                _services.ToggleTheme();
                ThemeItem.Content = Application.Current is App { Theme: ElementTheme.Light } ? S.ThemeDark : S.ThemeLight;
                break;
            case "Language":
                _services.ToggleLanguage();
                break;
        }
    }

    private void Show(Section section)
    {
        if (!_pages.TryGetValue(section, out UIElement? page))
        {
            page = section switch
            {
                Section.Tracker => new TrackerPage(_services.Tracker),
                Section.Picker => new TargetPickerPage(_services.Picker),
                Section.QuickScan => new QuickScanPage(_services.QuickScan),
                Section.SelectiveClean => new SelectiveCleanPage(_services.SelectiveClean),
                Section.Registry => new RegistryIntegrityPage(_services.Integrity),
                Section.Resources => new ResourceControlPage(_services.Resources),
                Section.Suspend => new ProcessSuspendPage(_services.Suspend),
                Section.Storage => new StorageMapPage(_services.Storage),
                Section.Duplicates => new DuplicateScanPage(_services.Duplicates),
                Section.Wipe => new SecureWipePage(_services.Wipe),
                Section.Repair => new RepairPage(_services.Repair),
                Section.History => new HistoryPage(_services.History),
                Section.Updates => new UpdatesPage(_services.Updates),
                _ => new AppsPage(_services.Apps),
            };
            _pages[section] = page;
        }

        PageHost.Child = page;
        switch (section)
        {
            case Section.Apps when _services.Apps.Inventory.Count == 0:
                _ = _services.Apps.LoadAsync();
                break;
            case Section.Tracker:
                _services.Tracker.Refresh();
                break;
            case Section.SelectiveClean:
                _services.SelectiveClean.EnsureLoaded();
                break;
            case Section.Resources when !_services.Resources.IsLoaded:
                _ = _services.Resources.RefreshAsync();
                break;
            case Section.Suspend when !_services.Suspend.IsLoaded:
                _ = _services.Suspend.RefreshAsync();
                break;
            case Section.History:
                _services.History.EnsureLoaded();
                break;
        }
    }

    private void OnPaletteOpened(object? sender, object e)
    {
        PalettePopup.HorizontalOffset = Math.Max(0, (ActualWidth - PaletteCard.Width) / 2);
        PalettePopup.VerticalOffset = PaletteTop;
        PaletteBox.Focus(FocusState.Programmatic);
    }

    private void OnPaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                _services.Palette.Move(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                _services.Palette.Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                _services.Palette.Run(null);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                _services.Palette.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteItemClick(object sender, ItemClickEventArgs e) => _services.Palette.Run(e.ClickedItem as PaletteEntry);
}
