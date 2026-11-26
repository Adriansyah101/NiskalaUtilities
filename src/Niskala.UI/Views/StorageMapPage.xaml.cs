using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class StorageMapPage : UserControl
{
    public StorageMapPage(StorageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public StorageViewModel ViewModel { get; }

    private void OnOpen(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem as StorageRow);

    private void OnReveal(object sender, RoutedEventArgs e) => ViewModel.RevealCommand.Execute((sender as FrameworkElement)?.Tag as StorageRow);
}
