using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class LeftoverReviewView : UserControl
{
    private readonly Attachment _events;

    public LeftoverReviewView()
    {
        _events = new Attachment(
            () => ViewModel.PropertyChanged += OnViewModelChanged,
            () => ViewModel.PropertyChanged -= OnViewModelChanged);
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _events.Attach();
            ShowTab();
        };
        Unloaded += (_, _) => _events.Detach();
    }

    public LeftoverReviewViewModel ViewModel
    {
        get;
        set
        {
            bool attached = _events.Detach();
            field = value;
            if (attached || IsLoaded)
            {
                _events.Attach();
            }

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
