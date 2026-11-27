using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Niskala.Interop.Desktop;

public static class WindowChrome
{
    private const int IdiApplication = 32512;

    public static unsafe bool Cloak(nint window, bool cloaked)
    {
        BOOL value = cloaked;
        return PInvoke.DwmSetWindowAttribute(new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL)).Succeeded;
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
}
