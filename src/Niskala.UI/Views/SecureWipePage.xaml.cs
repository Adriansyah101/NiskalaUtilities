using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class SecureWipePage : UserControl
{
    public SecureWipePage(WipeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public WipeViewModel ViewModel { get; }

    private void OnRemoveFile(object sender, RoutedEventArgs e) => ViewModel.RemoveFileCommand.Execute((sender as FrameworkElement)?.Tag as string);

    private void OnWipeFiles(object sender, RoutedEventArgs e) =>
        Confirm.Ask(WipeFilesButton, Strings.Current.WipeFilesConfirm, Strings.Current.WipeFiles, () => ViewModel.WipeFilesCommand.Execute(null));

    private void OnWipeFree(object sender, RoutedEventArgs e) =>
        Confirm.Ask(WipeFreeButton, Strings.Current.WipeFreeConfirm, Strings.Current.WipeFreeSpace, () => ViewModel.WipeFreeSpaceCommand.Execute(null));
}
