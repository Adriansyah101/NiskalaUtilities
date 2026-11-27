namespace Niskala.UI.Services;

public static class SplashHandoff
{
    public const string ReadyEvent = @"Local\NiskalaUtilities.SplashReady";

    public const int FramesBeforeReady = 3;

    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(5);

    public static bool IsReady(int renderedFrames, bool loaded) => loaded && renderedFrames >= FramesBeforeReady;

    public static bool Signal()
    {
        if (!EventWaitHandle.TryOpenExisting(ReadyEvent, out EventWaitHandle? ready))
        {
            return false;
        }

        using (ready)
        {
            return ready.Set();
        }
    }
}
