using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class DuplicateScanPage : UserControl
{
    public DuplicateScanPage(DuplicatesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public DuplicatesViewModel ViewModel { get; }

    private void OnRemoveFolder(object sender, RoutedEventArgs e) => ViewModel.RemoveFolderCommand.Execute((sender as FrameworkElement)?.Tag as string);

    private void OnRemove(object sender, RoutedEventArgs e) =>
        Confirm.Ask(RemoveButton, Strings.Current.RemoveCopiesConfirm, Strings.Current.RemoveCopies, () => ViewModel.RemoveCommand.Execute(null));
}
