using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Niskala.UI.Views;

internal static class Motion
{
    private static readonly UISettings Settings = new();

    public static bool Enabled => Settings.AnimationsEnabled;

    public static Task RunAsync(this Storyboard storyboard)
    {
        var done = new TaskCompletionSource();
        storyboard.Completed += OnCompleted;
        storyboard.Begin();
        return done.Task;

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            done.TrySetResult();
        }
    }

    public static Storyboard Tween(this Storyboard storyboard, DependencyObject target, string property, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
        return storyboard;
    }
}
