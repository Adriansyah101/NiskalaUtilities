using Niskala.Interop.Desktop;

namespace Niskala.Uninstall.Picker;

public static class ZOrderHitTest
{
    private const uint Transparent = 0x0000_0020;
    private const uint ToolWindow = 0x0000_0080;
    private const uint Layered = 0x0008_0000;
    private const uint NoActivate = 0x0800_0000;
    private const uint Caption = 0x00C0_0000;
    private const uint ThickFrame = 0x0004_0000;
    private const uint OverlayStyle = Layered | NoActivate | ToolWindow;

    public static nint? Pick(IEnumerable<WindowCandidate> zOrder, int x, int y, int skippedProcessId)
    {
        ArgumentNullException.ThrowIfNull(zOrder);
        var seen = new HashSet<nint>();
        foreach (WindowCandidate window in zOrder)
        {
            if (!seen.Add(window.Handle))
            {
                return null;
            }

            if (IsTarget(window, skippedProcessId) && window.Bounds.Contains(x, y))
            {
                return window.Handle;
            }
        }

        return null;
    }

    public static bool IsTarget(WindowCandidate window, int skippedProcessId) =>
        window.Visible
        && window.ProcessId != skippedProcessId
        && !window.Minimized
        && !window.Cloaked
        && (window.ExtendedStyle & Transparent) == 0
        && !((window.ExtendedStyle & Layered) != 0 && window.LayeredAlpha == 0)
        && !IsMonitorOverlay(window);

    public static bool IsMonitorOverlay(WindowCandidate window) =>
        (window.ExtendedStyle & OverlayStyle) == OverlayStyle
        && (window.Style & Caption) != Caption
        && (window.Style & ThickFrame) == 0
        && window.Monitor.Width > 0
        && window.Bounds.Left <= window.Monitor.Left
        && window.Bounds.Top <= window.Monitor.Top
        && window.Bounds.Right >= window.Monitor.Right
        && window.Bounds.Bottom >= window.Monitor.Bottom;
}
