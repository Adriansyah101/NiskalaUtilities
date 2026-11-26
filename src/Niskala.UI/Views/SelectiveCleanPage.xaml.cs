using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class SelectiveCleanPage : UserControl
{
    public SelectiveCleanPage(SelectiveCleanViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SelectiveCleanViewModel ViewModel { get; }

    private void OnClean(object sender, RoutedEventArgs e) =>
        Confirm.Ask(CleanButton, ViewModel.Permanent ? Strings.Current.CleanPermanentConfirm : Strings.Current.CleanConfirm, Strings.Current.CleanNow, () => ViewModel.CleanCommand.Execute(null));
}
