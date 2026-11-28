using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class UpdatesPage : UserControl
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        var events = new Attachment(() => viewModel.ReviewRequested += FocusApply, () => viewModel.ReviewRequested -= FocusApply);
        Loaded += (_, _) =>
        {
            events.Attach();
            FocusApply();
        };
        Unloaded += (_, _) => events.Detach();
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
