using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Niskala.UI.Views;

internal static class Confirm
{
    private const double Width = 320;

    public static void Ask(FrameworkElement anchor, string question, string action, Action confirmed)
    {
        var button = new Button
        {
            Content = action,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = Width,
                Spacing = 10,
                Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, button },
            },
        };
        button.Click += (_, _) =>
        {
            flyout.Hide();
            confirmed();
        };
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }
}
