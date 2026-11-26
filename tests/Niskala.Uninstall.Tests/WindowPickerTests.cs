using System.Runtime.InteropServices;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Interop.Desktop;
using Niskala.Uninstall.Picker;

namespace Niskala.Uninstall.Tests;

public sealed class WindowPickerTests
{
    private const uint WsPopup = 0x8000_0000;
    private const uint WsVisible = 0x1000_0000;
    private const uint WsExTopmost = 0x0000_0008;
    private const uint WsExToolWindow = 0x0000_0080;
    private const uint WsExTransparent = 0x0000_0020;
    private const uint WsExLayered = 0x0008_0000;
    private const uint LwaAlpha = 0x2;
    private const int Own = 4242;
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void A_package_matches_by_family_across_versions()
    {
        InstalledApp packaged = App("appx:Acme", null) with
        {
            Package = new PackageDetails("Acme.Photo_1.0.0.0_x64__8wekyb3d8bbwe", "Acme.Photo_8wekyb3d8bbwe", PackageSignatureKind.Store, null, null, false),
        };

        (InstalledApp? app, PickerMatch match) = WindowPicker.Match(@"C:\Program Files\WindowsApps\x\photo.exe", "Acme.Photo_2.0.0.0_x64__8wekyb3d8bbwe", [App("reg:other", @"C:\Program Files"), packaged]);

        Assert.Same(packaged, app);
        Assert.Equal(PickerMatch.Package, match);
    }

    [Fact]
    public void The_deepest_install_folder_wins_then_the_display_icon()
    {
        InstalledApp suite = App("reg:suite", @"C:\Apps\Suite");
        InstalledApp plugin = App("reg:plugin", @"C:\Apps\Suite\Plugin");
        InstalledApp tool = App("reg:tool", null) with { DisplayIcon = "\"C:\\Tools\\tool.exe\",0" };
        InstalledApp[] apps = [suite, plugin, tool];

        Assert.Equal((plugin, PickerMatch.InstallFolder), WindowPicker.Match(@"C:\Apps\Suite\Plugin\bin\p.exe", null, apps));
        Assert.Equal((suite, PickerMatch.InstallFolder), WindowPicker.Match(@"C:\Apps\Suite\s.exe", null, apps));
        Assert.Equal((tool, PickerMatch.DisplayIcon), WindowPicker.Match(@"C:\Tools\tool.exe", null, apps));
        Assert.Equal((null, PickerMatch.None), WindowPicker.Match(@"C:\Elsewhere\x.exe", null, apps));
    }

    [Fact]
    public void A_point_on_a_window_leads_to_its_process_and_app()
    {
        string image = Environment.ProcessPath!;
        InstalledApp self = App("reg:self", Path.GetDirectoryName(image));
        nint window = CreateWindowExW(WsExTopmost | WsExToolWindow, "BUTTON", "Niskala picker test", WsPopup | WsVisible, 120, 120, 160, 80, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            Assert.True(GetWindowRect(window, out Rect rect));
            PickerHit? hit = new WindowPicker(SystemWindowProbe.Instance, skippedProcessId: 0).PickAt((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, [self]);

            Assert.NotNull(hit);
            Assert.Equal(window, (nint)hit.Window);
            Assert.Equal(Environment.ProcessId, hit.ProcessId);
            Assert.Equal(image, hit.ImagePath, ignoreCase: true);
            Assert.Equal("Niskala picker test", hit.WindowTitle);
            Assert.Same(self, hit.App);
            Assert.Equal(PickerMatch.InstallFolder, hit.MatchedBy);
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public void Overlays_hidden_and_own_windows_are_looked_through()
    {
        WindowCandidate target = Window(9, 100);
        WindowCandidate[] zOrder =
        [
            Window(1, Own),
            Window(2, 200) with { ExtendedStyle = WsExTopmost | WsExTransparent | WsExLayered },
            Window(3, 201) with { ExtendedStyle = WsExTopmost | WsExLayered, LayeredAlpha = 0 },
            Window(4, 202) with { Visible = false },
            Window(5, 203) with { Minimized = true },
            Window(6, 204) with { Cloaked = true },
            Window(7, 205) with { Bounds = new ScreenRect(0, 0, 100, 100) },
            target,
            Window(10, 101),
        ];

        Assert.Equal(target.Handle, ZOrderHitTest.Pick(zOrder, 500, 500, Own));
    }

    [Fact]
    public void A_layered_window_that_can_be_seen_is_a_target()
    {
        WindowCandidate translucent = Window(1, 100) with { ExtendedStyle = WsExLayered, LayeredAlpha = 40 };
        WindowCandidate perPixel = Window(2, 100) with { ExtendedStyle = WsExLayered };

        Assert.True(ZOrderHitTest.IsTarget(translucent, Own));
        Assert.True(ZOrderHitTest.IsTarget(perPixel, Own));
        Assert.Equal(1, ZOrderHitTest.Pick([translucent, perPixel], 10, 10, Own));
    }

    [Fact]
    public void A_z_order_that_loops_ends_the_walk()
    {
        WindowCandidate a = Window(1, 100) with { Visible = false };
        WindowCandidate b = Window(2, 100) with { Visible = false };

        Assert.Null(ZOrderHitTest.Pick(Loop(a, b), 10, 10, Own));

        static IEnumerable<WindowCandidate> Loop(WindowCandidate first, WindowCandidate second)
        {
            while (true)
            {
                yield return first;
                yield return second;
            }
        }
    }

    [Fact]
    public void Real_click_through_overlays_are_looked_through()
    {
        nint below = CreateWindowExW(WsExTopmost | WsExToolWindow, "BUTTON", "Niskala picker below", WsPopup | WsVisible, 140, 140, 160, 80, 0, 0, 0, 0);
        nint clear = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExLayered, "BUTTON", "Niskala picker clear", WsPopup | WsVisible, 100, 100, 240, 160, 0, 0, 0, 0);
        nint through = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExTransparent, "BUTTON", "Niskala picker through", WsPopup | WsVisible, 100, 100, 240, 160, 0, 0, 0, 0);
        try
        {
            Assert.True(SetLayeredWindowAttributes(clear, 0, 0, LwaAlpha));
            Assert.True(GetWindowRect(below, out Rect rect));
            int x = (rect.Left + rect.Right) / 2;
            int y = (rect.Top + rect.Bottom) / 2;
            var picker = new WindowPicker(SystemWindowProbe.Instance, skippedProcessId: 0);

            WindowInfo? hit = picker.WindowAt(x, y);

            Assert.Equal(below, hit?.Handle);
            Assert.True(hit!.Value.Bounds.Contains(x, y));
            Assert.NotEqual(below, new WindowPicker(SystemWindowProbe.Instance).WindowAt(x, y)?.Handle);
        }
        finally
        {
            DestroyWindow(through);
            DestroyWindow(clear);
            DestroyWindow(below);
        }
    }

    private static InstalledApp App(string id, string? location) => new()
    {
        Id = id,
        Sources = AppSources.Registry,
        Scope = InstallScope.Machine,
        DisplayName = id,
        InstallLocation = location,
        InstallLocationGuard = location is null ? null : PathVerdict.Allowed,
    };

    private static WindowCandidate Window(nint handle, int processId) => new(handle, processId, Visible: true, Bounds: Screen);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);
}
