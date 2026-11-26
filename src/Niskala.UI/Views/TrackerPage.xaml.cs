using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class TrackerPage : UserControl
{
    public TrackerPage(TrackerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public TrackerViewModel ViewModel { get; }
}
