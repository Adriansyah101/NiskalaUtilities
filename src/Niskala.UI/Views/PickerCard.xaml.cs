using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Niskala.Interop.Desktop;
using Niskala.UI.ViewModels;
using Windows.Graphics;

namespace Niskala.UI.Views;

public sealed partial class PickerCard : Window
{
    private const int CardWidth = 420;
    private const int CardHeight = 132;
    private const int Margin = 28;

    public PickerCard(TargetPickerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Bindings.Update();
        Handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        double scale = WindowNative.Scale(Handle);
        AppWindow.Resize(new SizeInt32((int)(CardWidth * scale), (int)(CardHeight * scale)));
        if (Content is FrameworkElement root && Microsoft.UI.Xaml.Application.Current is App app)
        {
            root.RequestedTheme = app.Theme;
        }
    }

    public TargetPickerViewModel ViewModel { get; }

    public nint Handle { get; }

    public ScreenRect? ScreenBounds => AppWindow.IsVisible
        ? new ScreenRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Position.X + AppWindow.Size.Width, AppWindow.Position.Y + AppWindow.Size.Height)
        : null;

    public void ShowBeside(nint overlay, (int X, int Y)? cursor)
    {
        DisplayArea area = cursor is { } point
            ? DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        RectInt32 work = area.WorkArea;
        SizeInt32 size = AppWindow.Size;
        double scale = WindowNative.Scale(Handle);
        int margin = (int)(Margin * scale);
        bool low = cursor is { } c && c.Y > work.Y + (work.Height * 0.6);
        var position = new PointInt32(work.X + ((work.Width - size.Width) / 2), low ? work.Y + margin : work.Y + work.Height - size.Height - margin);
        if (AppWindow.Position != position)
        {
            AppWindow.Move(position);
        }

        if (!AppWindow.IsVisible && overlay != 0)
        {
            AppWindow.Show(false);
        }
    }
}
