using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Niskala.Interop.Desktop;

public static partial class WindowChrome
{
    private const int IdiApplication = 32512;

    private const uint DwmColorNone = 0xFFFFFFFE;

    private const int ThreadCurrent = 2;
    private const int ComNone = 0;

    private static nint s_dispatcherController;

    public static unsafe void ClearFrame(nint window)
    {
        var handle = new HWND(window);
        PInvoke.GetWindowRect(handle, out RECT bounds);
        PInvoke.GetClientRect(handle, out RECT client);
        var origin = default(System.Drawing.Point);
        PInvoke.ClientToScreen(handle, ref origin);
        int left = origin.X - bounds.left;
        int top = origin.Y - bounds.top;

        _ = PInvoke.SetWindowRgn(handle, PInvoke.CreateRectRgn(left, top, left + client.right, top + client.bottom), true);

        uint border = DwmColorNone;
        var corners = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;

        _ = PInvoke.DwmSetWindowAttribute(handle, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &border, sizeof(uint));
        _ = PInvoke.DwmSetWindowAttribute(handle, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        HRGN region = PInvoke.CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new DWM_BLURBEHIND
            {
                dwFlags = PInvoke.DWM_BB_ENABLE | PInvoke.DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = region,
            };
            PInvoke.DwmEnableBlurBehindWindow(handle, &blur).ThrowOnFailure();
        }
        finally
        {
            PInvoke.DeleteObject(region);
        }
    }

    public static void EnsureSystemDispatcherQueue()
    {
        if (s_dispatcherController != 0)
        {
            return;
        }

        var options = new DispatcherQueueOptions(Marshal.SizeOf<DispatcherQueueOptions>(), ThreadCurrent, ComNone);
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out s_dispatcherController));
    }

    public static unsafe nint ApplicationIcon()
    {
        HMODULE module = PInvoke.GetModuleHandle((PCWSTR)null);
        return PInvoke.LoadImage(
            new HINSTANCE(module.Value),
            (PCWSTR)(char*)IdiApplication,
            GDI_IMAGE_TYPE.IMAGE_ICON,
            0,
            0,
            IMAGE_FLAGS.LR_DEFAULTSIZE | IMAGE_FLAGS.LR_SHARED);
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DispatcherQueueOptions(int Size, int ThreadType, int ApartmentType);
}
