using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class RegistryIntegrityPage : UserControl
{
    public RegistryIntegrityPage(IntegrityViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public IntegrityViewModel ViewModel { get; }

    private void OnFix(object sender, RoutedEventArgs e) =>
        Confirm.Ask(FixButton, Strings.Current.RepairRegistryConfirm, Strings.Current.RepairSelected, () => ViewModel.FixCommand.Execute(null));
}
