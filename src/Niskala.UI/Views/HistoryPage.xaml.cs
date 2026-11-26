using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class HistoryPage : UserControl
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }
}
