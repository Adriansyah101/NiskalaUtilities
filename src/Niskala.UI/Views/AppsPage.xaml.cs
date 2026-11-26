using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class AppsPage : UserControl
{
    private const double NarrowPane = 460;
    private const double WidePane = 1040;
    private const double PaneGap = 72;

    public AppsPage(AppsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SizeChanged += (_, _) => SizePane();
        Loaded += (_, _) =>
        {
            viewModel.Inspector.PropertyChanged += OnInspectorChanged;
            SizePane();
        };
        Unloaded += (_, _) => viewModel.Inspector.PropertyChanged -= OnInspectorChanged;
    }

    public AppsViewModel ViewModel { get; }

    private void OnInspectorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorViewModel.IsWide))
        {
            SizePane();
        }
    }

    private void SizePane()
    {
        double room = Math.Max(NarrowPane, ActualWidth - PaneGap);
        Split.OpenPaneLength = Math.Min(ViewModel.Inspector.IsWide ? WidePane : NarrowPane, room);
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out AppFilter filter))
        {
            ViewModel.SelectFilter(filter);
        }
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out AppSort sort))
        {
            ViewModel.Sort = sort;
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is AppItem item)
        {
            ViewModel.Realized(item);
        }
    }
}
