using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Niskala.Interop.Desktop;

public sealed unsafe class PickerOverlay : IDisposable
{
    private const string WindowClass = "NiskalaPickerOverlay";
    private const uint Dim = 0x66000000;
    private const uint Clear = 0x01000000;
    private const uint Open = 0x00000000;
    private const int BorderWidth = 3;
    private const byte Opaque = 255;

    private static ushort s_atom;
    private static PickerOverlay? s_current;

    private readonly SynchronizationContext _context;
    private readonly uint _accent;
    private readonly ScreenRect _screen;
    private HWND _window;
    private HHOOK _mouseHook;
    private HHOOK _keyboardHook;
    private HDC _memory;
    private HBITMAP _bitmap;
    private HGDIOBJ _previous;
    private uint* _bits;
    private ScreenRect? _highlight;
    private ScreenRect[] _passThrough = [];
    private bool _drawn;
    private int _movePending;

    private PickerOverlay(SynchronizationContext context, uint accent, ScreenRect screen)
    {
        _context = context;
        _accent = accent;
        _screen = screen;
    }

    public event Action<int, int>? Moved;

    public event Action<int, int>? Picked;

    public event Action? Cancelled;

    public nint Handle => (nint)_window.Value;

    public static PickerOverlay Show(uint accentRgb)
    {
        SynchronizationContext context = SynchronizationContext.Current ?? throw new InvalidOperationException("The overlay needs a thread with a synchronization context.");
        var screen = new ScreenRect(
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN) + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN) + PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
        var overlay = new PickerOverlay(context, 0xFF000000 | accentRgb, screen);
        overlay.Create();
        return overlay;
    }

    public void PassThrough(IReadOnlyList<ScreenRect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        if (rectangles.SequenceEqual(_passThrough))
        {
            return;
        }

        _passThrough = [.. rectangles];
        _drawn = false;
        Highlight(_highlight);
    }

    public void Highlight(ScreenRect? target)
    {
        if (_drawn && target == _highlight)
        {
            return;
        }

        _highlight = target;
        _drawn = true;
        int width = _screen.Width;
        var pixels = new Span<uint>(_bits, width * _screen.Height);
        pixels.Fill(Dim);
        if (target is { } t)
        {
            int left = Math.Clamp(t.Left - _screen.Left, 0, width);
            int right = Math.Clamp(t.Right - _screen.Left, 0, width);
            int top = Math.Clamp(t.Top - _screen.Top, 0, _screen.Height);
            int bottom = Math.Clamp(t.Bottom - _screen.Top, 0, _screen.Height);
            for (int y = top; y < bottom; y++)
            {
                Span<uint> row = pixels.Slice((y * width) + left, right - left);
                bool edge = y - top < BorderWidth || bottom - y <= BorderWidth;
                if (edge)
                {
                    row.Fill(_accent);
                    continue;
                }

                row.Fill(Clear);
                row[..Math.Min(BorderWidth, row.Length)].Fill(_accent);
                row[Math.Max(row.Length - BorderWidth, 0)..].Fill(_accent);
            }
        }

        foreach (ScreenRect open in _passThrough)
        {
            Fill(pixels, open, Open);
        }

        Present();
    }

    private void Fill(Span<uint> pixels, ScreenRect area, uint color)
    {
        int width = _screen.Width;
        int left = Math.Clamp(area.Left - _screen.Left, 0, width);
        int right = Math.Clamp(area.Right - _screen.Left, 0, width);
        int top = Math.Clamp(area.Top - _screen.Top, 0, _screen.Height);
        int bottom = Math.Clamp(area.Bottom - _screen.Top, 0, _screen.Height);
        for (int y = top; y < bottom; y++)
        {
            pixels.Slice((y * width) + left, right - left).Fill(color);
        }
    }

    private bool IsPassThrough(System.Drawing.Point point)
    {
        foreach (ScreenRect open in _passThrough)
        {
            if (open.Contains(point.X, point.Y))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            Unhook(ref _mouseHook);
            Unhook(ref _keyboardHook);
        }
        finally
        {
            if (!_window.IsNull)
            {
                PInvoke.DestroyWindow(_window);
                _window = HWND.Null;
            }

            if (!_memory.IsNull)
            {
                PInvoke.SelectObject(_memory, _previous);
                PInvoke.DeleteObject(_bitmap);
                PInvoke.DeleteDC(_memory);
                _memory = HDC.Null;
            }

            if (s_current == this)
            {
                s_current = null;
            }
        }
    }

    private static void Unhook(ref HHOOK hook)
    {
        if (!hook.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(hook);
            hook = HHOOK.Null;
        }
    }

    private void Create()
    {
        HINSTANCE instance = (HINSTANCE)(nint)PInvoke.GetModuleHandle((char*)null).Value;
        if (s_atom == 0)
        {
            fixed (char* name = WindowClass)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = instance,
                    lpszClassName = name,
                };
                s_atom = PInvoke.RegisterClassEx(windowClass);
            }

            if (s_atom == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }
        }

        BITMAPINFO info = default;
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = _screen.Width;
        info.bmiHeader.biHeight = -_screen.Height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        HDC screenDc = PInvoke.GetDC(HWND.Null);
        _memory = PInvoke.CreateCompatibleDC(screenDc);
        void* bits;
        _bitmap = PInvoke.CreateDIBSection(screenDc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        PInvoke.ReleaseDC(HWND.Null, screenDc);
        if (_bitmap.IsNull)
        {
            Dispose();
            throw new InvalidOperationException("The overlay bitmap could not be created.");
        }

        _bits = (uint*)bits;
        _previous = PInvoke.SelectObject(_memory, _bitmap);

        fixed (char* name = WindowClass)
        {
            _window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                name,
                name,
                WINDOW_STYLE.WS_POPUP,
                _screen.Left,
                _screen.Top,
                _screen.Width,
                _screen.Height,
                HWND.Null,
                HMENU.Null,
                instance,
                null);
        }

        if (_window.IsNull)
        {
            int error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new InvalidOperationException($"CreateWindowEx failed with Win32 error {error}.");
        }

        s_current = this;
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, instance, 0);
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, instance, 0);
        if (_mouseHook.IsNull || _keyboardHook.IsNull)
        {
            int error = Marshal.GetLastPInvokeError();
            Dispose();
            throw new InvalidOperationException($"SetWindowsHookEx failed with Win32 error {error}.");
        }

        Highlight(null);
        PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_window);
        PInvoke.SetFocus(_window);
    }

    private void Present()
    {
        var position = new System.Drawing.Point(_screen.Left, _screen.Top);
        var size = new SIZE(_screen.Width, _screen.Height);
        var source = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = Opaque, AlphaFormat = 1 };
        HDC screenDc = PInvoke.GetDC(HWND.Null);
        PInvoke.UpdateLayeredWindow(_window, screenDc, &position, &size, _memory, &source, new COLORREF(0), &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        PInvoke.ReleaseDC(HWND.Null, screenDc);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_SETCURSOR && s_current is { } overlay && overlay._window == window)
        {
            PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS));
            return new LRESULT(1);
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && s_current is { } overlay)
        {
            System.Drawing.Point point = ((MSLLHOOKSTRUCT*)lParam.Value)->pt;
            if (overlay.IsPassThrough(point))
            {
                if ((uint)wParam.Value == PInvoke.WM_MOUSEMOVE)
                {
                    overlay.PostMove();
                }

                return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
            }

            switch ((uint)wParam.Value)
            {
                case PInvoke.WM_MOUSEMOVE:
                    overlay.PostMove();
                    break;
                case PInvoke.WM_LBUTTONDOWN:
                    overlay._context.Post(_ => overlay.Picked?.Invoke(point.X, point.Y), null);
                    return new LRESULT(1);
                case PInvoke.WM_RBUTTONDOWN:
                    overlay.PostCancel();
                    return new LRESULT(1);
                case PInvoke.WM_LBUTTONUP:
                case PInvoke.WM_RBUTTONUP:
                    return new LRESULT(1);
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && s_current is { } overlay && (VIRTUAL_KEY)((KBDLLHOOKSTRUCT*)lParam.Value)->vkCode == VIRTUAL_KEY.VK_ESCAPE)
        {
            if ((uint)wParam.Value is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN)
            {
                overlay.PostCancel();
            }

            return new LRESULT(1);
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private void PostCancel() => _context.Post(static state => ((PickerOverlay)state!).Cancelled?.Invoke(), this);

    private void PostMove()
    {
        if (Interlocked.Exchange(ref _movePending, 1) == 0)
        {
            _context.Post(
                static state =>
                {
                    var overlay = (PickerOverlay)state!;
                    Volatile.Write(ref overlay._movePending, 0);
                    PInvoke.GetCursorPos(out System.Drawing.Point point);
                    overlay.Moved?.Invoke(point.X, point.Y);
                },
                this);
        }
    }
}
