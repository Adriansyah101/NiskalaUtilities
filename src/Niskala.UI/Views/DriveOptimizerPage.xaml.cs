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
        var events = new Attachment(
            () =>
            {
                ViewModel.MapChanged += Redraw;
                ViewModel.PropertyChanged += OnViewModelChanged;
                Map.CellPicked += OnCellPicked;
            },
            () =>
            {
                ViewModel.MapChanged -= Redraw;
                ViewModel.PropertyChanged -= OnViewModelChanged;
                Map.CellPicked -= OnCellPicked;
                Map.Show(null);
            });
        Loaded += (_, _) =>
        {
            events.Attach();
            SyncTab();
            Redraw();
        };
        Unloaded += (_, _) => events.Detach();
    }

    public DriveOptimizerViewModel ViewModel { get; }

    private void Redraw() => Map.Show(ViewModel.Summarize, ViewModel.HighlightCell);

    private void OnCellPicked(int cell, int cells) => _ = ViewModel.InspectAsync(cell, cells);

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriveOptimizerViewModel.Tab) or nameof(DriveOptimizerViewModel.Selected) or nameof(DriveOptimizerViewModel.IsHdd))
        {
            SyncTab();
        }
    }

    private void SyncTab()
    {
        SelectorBarItem wanted = ViewModel.Tab switch
        {
            DriveOptimizerViewModel.TabFragments when ViewModel.IsHdd => FragmentsTab,
            DriveOptimizerViewModel.TabSummary => SummaryTab,
            _ => MapTab,
        };
        if (Tabs.SelectedItem != wanted)
        {
            Tabs.SelectedItem = wanted;
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Tab = sender.SelectedItem == FragmentsTab ? DriveOptimizerViewModel.TabFragments
            : sender.SelectedItem == SummaryTab ? DriveOptimizerViewModel.TabSummary
            : DriveOptimizerViewModel.TabMap;

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

    private void OnOptimizeQueue(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanQueueNow)
        {
            return;
        }

        if (!ViewModel.QueueMovesClusters)
        {
            ViewModel.OptimizeQueueCommand.Execute(null);
            return;
        }

        Confirm.Ask(QueueButton, Strings.Current.QueueConfirm, ViewModel.QueueOptimizeText, () => ViewModel.OptimizeQueueCommand.Execute(null));
    }

    private void OnApplyGuide(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanApplyGuideNow)
        {
            return;
        }

        if (!ViewModel.GuideMovesClusters)
        {
            ViewModel.ApplyGuideCommand.Execute(null);
            return;
        }

        Confirm.Ask(ApplyGuideButton, Strings.Current.DriveDefragConfirm, Strings.Current.GuideApplyRecommendation, () => ViewModel.ApplyGuideCommand.Execute(null));
    }

    private void OnDefragChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanDefragCheckedNow)
        {
            Confirm.Ask(CheckedButton, Strings.Current.DriveDefragConfirm, Strings.Current.DefragChecked, () => ViewModel.DefragCheckedCommand.Execute(null));
        }
    }
}
