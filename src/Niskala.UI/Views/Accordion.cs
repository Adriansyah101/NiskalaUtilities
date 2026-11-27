using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Niskala.UI.Services;
using Windows.Foundation;

namespace Niskala.UI.Views;

public sealed partial class Accordion : UserControl
{
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(Accordion), new PropertyMetadata(false, OnExpandedChanged));

    private Storyboard? _motion;

    public Accordion()
    {
        Visibility = Visibility.Collapsed;
        IsTabStop = false;
        SizeChanged += (_, _) => Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private static void OnExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((Accordion)sender).Slide((bool)e.NewValue);

    private void Slide(bool expanded)
    {
        bool shown = Visibility == Visibility.Visible;
        double from = shown ? ActualHeight : 0;
        double fade = shown ? Opacity : 0;
        _motion?.Stop();
        _motion = null;

        double natural = 0;
        if (Content is UIElement content)
        {
            content.Measure(new Size(ActualWidth > 0 ? ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
            natural = content.DesiredSize.Height;
        }

        double to = NavMotion.AccordionTarget(expanded, natural);
        TimeSpan duration = NavMotion.Remaining(from, to, natural, NavMotion.Accordion, IsLoaded && Motion.Enabled);
        if (duration == TimeSpan.Zero)
        {
            Settle(expanded);
            return;
        }

        Height = from;
        Opacity = fade;
        Visibility = Visibility.Visible;
        Storyboard motion = new Storyboard()
            .Tween(this, nameof(Height), from, to, duration)
            .Tween(this, nameof(Opacity), fade, expanded ? 1 : 0, duration);
        motion.Completed += (_, _) =>
        {
            if (_motion == motion)
            {
                _motion = null;
                motion.Stop();
                Settle(expanded);
            }
        };
        _motion = motion;
        motion.Begin();
    }

    private void Settle(bool expanded)
    {
        ClearValue(HeightProperty);
        Opacity = 1;
        Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
