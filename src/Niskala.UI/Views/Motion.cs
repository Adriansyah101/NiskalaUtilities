using Microsoft.UI.Xaml.Media.Animation;

namespace Niskala.UI.Views;

internal static class Motion
{
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
}
