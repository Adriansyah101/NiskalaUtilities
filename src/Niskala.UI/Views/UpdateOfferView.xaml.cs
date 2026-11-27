using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class UpdateOfferView : UserControl
{
    public UpdateOfferView(UpdateOffer offer)
    {
        Offer = offer;
        InitializeComponent();
    }

    public UpdateOffer Offer { get; }

    public Strings S => Strings.Current;

    public static async Task<UpdatePromptChoice> AskAsync(XamlRoot root, ElementTheme theme, UpdateOffer offer)
    {
        Strings s = Strings.Current;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = theme,
            Title = s.UpdatePromptTitle(offer.Latest),
            Content = new UpdateOfferView(offer),
            PrimaryButtonText = s.UpdateNow,
            SecondaryButtonText = s.UpdateReview,
            CloseButtonText = s.UpdateLater,
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UpdatePromptChoice.UpdateNow,
            ContentDialogResult.Secondary => UpdatePromptChoice.Review,
            _ => UpdatePromptChoice.Later,
        };
    }
}
