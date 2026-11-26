using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class QuickScanPage : UserControl
{
    public QuickScanPage(QuickScanViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public QuickScanViewModel ViewModel { get; }

    private void OnClean(object sender, RoutedEventArgs e) =>
        Confirm.Ask(CleanButton, Strings.Current.CleanConfirm, Strings.Current.CleanNow, () => ViewModel.CleanCommand.Execute(null));
}
