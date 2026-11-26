using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Niskala.Interop.Desktop;
using Windows.Graphics;

namespace Niskala.UI.Views;

public sealed partial class SplashWindow : Window
{
    private const int CardWidth = 560;
    private const int CardHeight = 320;

    private readonly nint _handle;

    public SplashWindow()
    {
        InitializeComponent();
        _handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        double scale = WindowNative.Scale(_handle);
        AppWindow.ResizeClient(new SizeInt32((int)(CardWidth * scale), (int)(CardHeight * scale)));
        SizeInt32 size = AppWindow.Size;
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(work.X + ((work.Width - size.Width) / 2), work.Y + ((work.Height - size.Height) / 2)));

        SystemBackdrop = new ClearBackdrop();
    }

    public Task ShowAsync()
    {
        Activate();
        WindowChrome.ClearFrame(_handle);
        return Task.WhenAll(Mark.PlayIntroAsync(), Intro.RunAsync());
    }

    public Task FadeOutAsync() => Outro.RunAsync();

    private sealed partial class ClearBackdrop : SystemBackdrop
    {
        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            WindowChrome.EnsureSystemDispatcherQueue();
            connectedTarget.SystemBackdrop = new Windows.UI.Composition.Compositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}
