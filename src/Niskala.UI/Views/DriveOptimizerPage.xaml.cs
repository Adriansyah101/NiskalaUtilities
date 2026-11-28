using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class DriveOptimizerPage : UserControl
{
    public DriveOptimizerPage(DriveOptimizerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        SyncTab();
        Loaded += (_, _) =>
        {
            ViewModel.MapChanged += Redraw;
            ViewModel.PropertyChanged += OnViewModelChanged;
            Map.Show(ViewModel.Summarize);
        };
        Unloaded += (_, _) =>
        {
            ViewModel.MapChanged -= Redraw;
            ViewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    public DriveOptimizerViewModel ViewModel { get; }

    private void Redraw() => Map.Show(ViewModel.Summarize);

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriveOptimizerViewModel.Tab) or nameof(DriveOptimizerViewModel.Selected))
        {
            SyncTab();
        }
    }

    private void SyncTab()
    {
        SelectorBarItem wanted = ViewModel.Tab == DriveOptimizerViewModel.TabFragments && ViewModel.IsHdd ? FragmentsTab : SummaryTab;
        if (Tabs.SelectedItem != wanted)
        {
            Tabs.SelectedItem = wanted;
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Tab = sender.SelectedItem == FragmentsTab ? DriveOptimizerViewModel.TabFragments : DriveOptimizerViewModel.TabSummary;

    private void OnBannerClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Banner.Clear();

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanPrimary)
        {
            return;
        }

        if (ViewModel.IsSsd)
        {
            ViewModel.OptimizeCommand.Execute(null);
            return;
        }

        Confirm.Ask(PrimaryButton, Strings.Current.DriveDefragConfirm, ViewModel.PrimaryText, () => ViewModel.OptimizeCommand.Execute(null));
    }

    private void OnDefragChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanDefragCheckedNow)
        {
            Confirm.Ask(CheckedButton, Strings.Current.DriveDefragConfirm, Strings.Current.DefragChecked, () => ViewModel.DefragCheckedCommand.Execute(null));
        }
    }
}
