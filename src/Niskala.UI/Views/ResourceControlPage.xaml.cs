using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class ResourceControlPage : UserControl
{
    public ResourceControlPage(ResourcesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ResourcesViewModel ViewModel { get; }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Tab = sender.SelectedItem is { } item ? sender.Items.IndexOf(item) : 0;

    private void OnOpenLocation(object sender, RoutedEventArgs e) => ViewModel.OpenLocationCommand.Execute((sender as FrameworkElement)?.Tag as ResourceRow);

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResourceRow row } item)
        {
            return;
        }

        Confirm.Ask(List.ContainerFromItem(row) as FrameworkElement ?? item, Strings.Current.RemoveStartupConfirm, Strings.Current.Delete, () => ViewModel.RemoveCommand.Execute(row));
    }
}
