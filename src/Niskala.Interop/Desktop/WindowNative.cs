using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Niskala.Interop.Desktop;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct WindowCandidate(
    nint Handle,
    int ProcessId,
    bool Visible,
    bool Minimized = false,
    bool Cloaked = false,
    uint ExtendedStyle = 0,
    byte? LayeredAlpha = null,
    ScreenRect Bounds = default,
    uint Style = 0,
    ScreenRect Monitor = default);

public readonly record struct WindowInfo(nint Handle, int ProcessId, string ClassName, string Title, ScreenRect Bounds = default);

public static class WindowNative
{
    private const int TextCapacity = 512;
    private const uint BroadcastTimeoutMs = 5000;
    private const uint MessageTimeoutMs = 1000;
    private const nint HwndBroadcast = 0xFFFF;
    private const nint HwndMessage = -3;
    private const string FrameHostClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    public static IEnumerable<WindowCandidate> ZOrder()
    {
        for (HWND window = PInvoke.GetTopWindow(HWND.Null); !window.IsNull; window = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_HWNDNEXT))
        {
            yield return Candidate(window);
        }
    }

    public static WindowInfo? Describe(nint window) => Describe(new HWND(window));

    public static unsafe IReadOnlyList<nint> ProcessWindows(int processId)
    {
        var found = new ProcessWindowList(processId);
        GCHandle handle = GCHandle.Alloc(found);
        try
        {
            PInvoke.EnumWindows(&CollectProcessWindow, (nint)handle);
        }
        finally
        {
            handle.Free();
        }

        for (HWND window = PInvoke.FindWindowEx(new HWND(HwndMessage), HWND.Null, (string?)null, null); !window.IsNull; window = PInvoke.FindWindowEx(new HWND(HwndMessage), window, (string?)null, null))
        {
            found.Add(window);
        }

        return found.Windows;
    }

    public static ScreenRect ToScreen(nint window, ScreenRect client)
    {
        var topLeft = new System.Drawing.Point(client.Left, client.Top);
        var bottomRight = new System.Drawing.Point(client.Right, client.Bottom);
        PInvoke.ClientToScreen(new HWND(window), ref topLeft);
        PInvoke.ClientToScreen(new HWND(window), ref bottomRight);
        return new ScreenRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    public static (int X, int Y) ToClient(nint window, int x, int y)
    {
        var point = new System.Drawing.Point(x, y);
        PInvoke.ScreenToClient(new HWND(window), ref point);
        return (point.X, point.Y);
    }

    internal static nuint? Send(nint window, uint message, nuint wParam, nint lParam) =>
        PInvoke.SendMessageTimeout(new HWND(window), message, wParam, lParam, SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, MessageTimeoutMs, out nuint result) == 0 ? null : result;

    private static unsafe WindowCandidate Candidate(HWND window)
    {
        PInvoke.GetWindowThreadProcessId(window, out uint processId);
        if (!PInvoke.IsWindowVisible(window))
        {
            return new WindowCandidate((nint)window.Value, (int)processId, Visible: false);
        }

        uint extended = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        uint cloaked = 0;
        bool isCloaked = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0;
        byte? alpha = null;
        if ((extended & (uint)WINDOW_EX_STYLE.WS_EX_LAYERED) != 0)
        {
            byte constant = 0;
            LAYERED_WINDOW_ATTRIBUTES_FLAGS flags = 0;
            if (PInvoke.GetLayeredWindowAttributes(window, null, &constant, &flags) && (flags & LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA) != 0)
            {
                alpha = constant;
            }
        }

        uint style = (uint)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return new WindowCandidate((nint)window.Value, (int)processId, true, PInvoke.IsIconic(window), isCloaked, extended, alpha, Bounds(window), style, MonitorOf(window));
    }

    private static ScreenRect MonitorOf(HWND window)
    {
        var info = new MONITORINFO { cbSize = (uint)Unsafe.SizeOf<MONITORINFO>() };
        HMONITOR monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetMonitorInfo(monitor, ref info)
            ? new ScreenRect(info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right, info.rcMonitor.bottom)
            : default;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL CollectProcessWindow(HWND window, LPARAM state)
    {
        ((ProcessWindowList)GCHandle.FromIntPtr(state).Target!).Add(window);
        return true;
    }

    private static WindowInfo? Describe(HWND root)
    {
        string className = ClassName(root);

        HWND content = root;
        if (className == FrameHostClass && PInvoke.FindWindowEx(root, HWND.Null, CoreWindowClass, null) is { IsNull: false } core)
        {
            content = core;
        }

        PInvoke.GetWindowThreadProcessId(content, out uint processId);
        return processId == 0 ? null : new WindowInfo(root, (int)processId, className, Title(root), Bounds(root));
    }

    private static unsafe ScreenRect Bounds(HWND window)
    {
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Failed
            && !PInvoke.GetWindowRect(window, out rect))
        {
            return default;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    public static double Scale(nint window)
    {
        uint dpi = PInvoke.GetDpiForWindow(new HWND(window));
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static unsafe void BroadcastSettingChange(string area)
    {
        ArgumentNullException.ThrowIfNull(area);
        fixed (char* name = area)
        {
            PInvoke.SendMessageTimeout(
                new HWND(HwndBroadcast),
                PInvoke.WM_SETTINGCHANGE,
                default,
                (nint)name,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
                BroadcastTimeoutMs,
                null);
        }
    }

    private static string ClassName(HWND window)
    {
        Span<char> buffer = stackalloc char[TextCapacity];
        int length = PInvoke.GetClassName(window, buffer);
        return new string(buffer[..Math.Max(length, 0)]);
    }

    private static string Title(HWND window)
    {
        Span<char> buffer = stackalloc char[TextCapacity];
        int length = PInvoke.GetWindowText(window, buffer);
        return new string(buffer[..Math.Max(length, 0)]);
    }

    private sealed unsafe class ProcessWindowList(int processId)
    {
        public List<nint> Windows { get; } = [];

        public void Add(HWND window)
        {
            PInvoke.GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)processId)
            {
                Windows.Add((nint)window.Value);
            }
        }
    }
}
