using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class LeftoverReviewView : UserControl
{
    public LeftoverReviewView()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowTab();
    }

    public LeftoverReviewViewModel ViewModel
    {
        get;
        set
        {
            field.PropertyChanged -= OnViewModelChanged;
            field = value;
            field.PropertyChanged += OnViewModelChanged;
            Bindings.Update();
            ShowTab();
        }
    } = new();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LeftoverReviewViewModel.Tab) or "")
        {
            ShowTab();
        }
    }

    private void ShowTab()
    {
        SelectorBarItem item = ViewModel.Tab == ReviewTab.Files ? FilesTab : RegistryTab;
        if (Tabs.SelectedItem != item)
        {
            Tabs.SelectedItem = item;
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Tab = sender.SelectedItem == RegistryTab ? ReviewTab.Registry : ReviewTab.Files;
}
