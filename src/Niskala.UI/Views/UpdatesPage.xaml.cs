using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class UpdatesPage : UserControl
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public UpdatesViewModel ViewModel { get; }
}
