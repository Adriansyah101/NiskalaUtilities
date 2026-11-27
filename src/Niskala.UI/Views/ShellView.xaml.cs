using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;
using Windows.System;

namespace Niskala.UI.Views;

public sealed partial class ShellView : UserControl
{
    private const double PaletteTop = 56;

    private readonly UiServices _services;
    private readonly Dictionary<Section, UIElement> _pages = [];
    private Storyboard? _paneMotion;

    public ShellView(UiServices services)
    {
        _services = services;
        InitializeComponent();
        LabelThemeButton();
        Pane.Width = Nav.PaneWidth;
        Nav.Shown += Show;
        Nav.PropertyChanged += OnNavChanged;
        services.ThemeToggleRequested += LabelThemeButton;
        Unloaded += (_, _) =>
        {
            Nav.Shown -= Show;
            Nav.PropertyChanged -= OnNavChanged;
            services.ThemeToggleRequested -= LabelThemeButton;
        };
        Show(Nav.Selected);
    }

    public Strings S => Strings.Current;

    public NavigationViewModel Nav => _services.Navigation;

    public PaletteViewModel Palette => _services.Palette;

    public UpdatesViewModel Updates => _services.Updates;

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => Nav.TogglePane();

    private void OnNavChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationViewModel.PaneWidth))
        {
            ResizePane();
        }
    }

    private void ResizePane()
    {
        double from = Pane.ActualWidth > 0 ? Pane.ActualWidth : Pane.Width;
        double to = Nav.PaneWidth;
        _paneMotion?.Stop();
        _paneMotion = null;
        TimeSpan duration = NavMotion.Remaining(from, to, NavigationViewModel.OpenPaneWidth - NavigationViewModel.CompactPaneWidth, NavMotion.Pane, IsLoaded && Motion.Enabled);
        if (duration == TimeSpan.Zero)
        {
            Pane.Width = to;
            return;
        }

        Pane.Width = from;
        Storyboard motion = new Storyboard().Tween(Pane, nameof(Width), from, to, duration);
        motion.Completed += (_, _) =>
        {
            if (_paneMotion == motion)
            {
                _paneMotion = null;
                motion.Stop();
                Pane.Width = to;
            }
        };
        _paneMotion = motion;
        motion.Begin();
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavCategory category } button)
        {
            return;
        }

        if (Nav.IsPaneOpen)
        {
            Nav.Toggle(category);
            return;
        }

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.Items.Add(new MenuFlyoutItem { Text = category.Title, IsEnabled = false });
        foreach (NavPage page in category.Pages)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = page.Title,
                Icon = new FontIcon { Glyph = page.Glyph },
                GroupName = category.Group.ToString(),
                IsChecked = page.IsSelected,
            };
            item.Click += (_, _) => Nav.Select(page.Section);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void OnPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavPage page })
        {
            Nav.Select(page.Section);
        }
    }

    private void OnUpdatesClick(object sender, RoutedEventArgs e) => Nav.Select(Section.Updates);

    private void OnThemeClick(object sender, RoutedEventArgs e) => _services.ToggleTheme();

    private void OnLanguageClick(object sender, RoutedEventArgs e) => _services.ToggleLanguage();

    private void LabelThemeButton() => ThemeButton.Label = Application.Current is App { Theme: ElementTheme.Light } ? S.ThemeDark : S.ThemeLight;

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
