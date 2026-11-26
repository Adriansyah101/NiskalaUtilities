using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class TargetPickerPage : UserControl
{
    public TargetPickerPage(TargetPickerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public TargetPickerViewModel ViewModel { get; }
}
