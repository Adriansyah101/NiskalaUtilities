using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class UpdatesPage : UserControl
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            viewModel.ReviewRequested += FocusApply;
            FocusApply();
        };
        Unloaded += (_, _) => viewModel.ReviewRequested -= FocusApply;
    }

    public UpdatesViewModel ViewModel { get; }

    private void FocusApply()
    {
        if (!ViewModel.TakeReview())
        {
            return;
        }

        ApplyButton.StartBringIntoView();
        ApplyButton.Focus(FocusState.Keyboard);
    }
}
