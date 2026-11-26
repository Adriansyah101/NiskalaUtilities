using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class ProcessSuspendPage : UserControl
{
    public ProcessSuspendPage(SuspendViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SuspendViewModel ViewModel { get; }
}
