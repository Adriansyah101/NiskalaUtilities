using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian.Startup;
using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;
using Niskala.Interop.Shell;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Picker;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Tests;

public sealed class TargetPickerTests : IDisposable
{
    private const uint Layered = 0x0008_0000;
    private const uint NoActivate = 0x0800_0000;
    private const uint ToolWindow = 0x0000_0080;
    private const uint Topmost = 0x0000_0008;
    private const uint Caption = 0x00C0_0000;
    private const uint Popup = 0x8000_0000;
    private const int Desktop = 10;
    private const int Taskbar = 20;
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("niskala-picker-");

    public void Dispose() => _scratch.Delete(recursive: true);

    [Fact]
    public void A_see_through_overlay_over_a_whole_monitor_is_looked_through()
    {
        var overlay = new WindowCandidate(1, 300, Visible: true, ExtendedStyle: Layered | NoActivate | ToolWindow | Topmost, Bounds: Monitor, Style: Popup, Monitor: Monitor);
        var target = new WindowCandidate(2, 400, Visible: true, Bounds: Monitor, Monitor: Monitor);

        Assert.True(ZOrderHitTest.IsMonitorOverlay(overlay));
        Assert.Equal(target.Handle, ZOrderHitTest.Pick([overlay, target], 900, 500, skippedProcessId: 0));
    }

    [Fact]
    public void Overlay_lookalikes_stay_targets()
    {
        var overlay = new WindowCandidate(1, 300, Visible: true, ExtendedStyle: Layered | NoActivate | ToolWindow, Bounds: Monitor, Style: Popup, Monitor: Monitor);

        Assert.False(ZOrderHitTest.IsMonitorOverlay(overlay with { ExtendedStyle = Layered | ToolWindow }));
        Assert.False(ZOrderHitTest.IsMonitorOverlay(overlay with { Style = Caption }));
        Assert.False(ZOrderHitTest.IsMonitorOverlay(overlay with { Bounds = new ScreenRect(0, 0, 1920, 1040) }));
        Assert.False(ZOrderHitTest.IsMonitorOverlay(overlay with { Monitor = default }));
        Assert.True(ZOrderHitTest.IsMonitorOverlay(overlay with { Bounds = new ScreenRect(-8, -8, 1928, 1088) }));
    }

    [Fact]
    public void A_desktop_icon_is_hit_over_its_picture_and_its_label()
    {
        var view = new DesktopIconView(77, [new DesktopIconSlot(0, "Recycle Bin", new ScreenRect(0, 5, 76, 75)), new DesktopIconSlot(1, "Photo Editor", new ScreenRect(0, 105, 76, 190))]);

        Assert.Equal("Photo Editor", view.At(38, 110)?.Label);
        Assert.Equal("Photo Editor", view.At(75, 189)?.Label);
        Assert.Null(view.At(76, 150));
        Assert.Null(view.At(30, 90));
    }

    [Fact]
    public void A_point_on_the_desktop_leads_through_the_icon_to_the_shortcut_and_its_app()
    {
        InstalledApp editor = App("reg:editor", Path.Combine(_scratch.FullName, "Editor"));
        string exe = Program(editor, "editor.exe");
        string link = Shortcut("Photo Editor.lnk", exe);
        var surfaces = new FakeSurfaces
        {
            View = new DesktopIconView(77, [new DesktopIconSlot(0, "Photo Editor", new ScreenRect(0, 105, 76, 190))]),
            Items = [new DesktopItem(Path.Combine(_scratch.FullName, "Other.lnk"), "Other"), new DesktopItem(link, "Photo Editor")],
            ClientOffset = (100, 0),
        };
        var picker = new WindowPicker(new FakeProbe(), surfaces, skippedProcessId: 1);
        picker.ReadDesktop();

        PickerHit? hit = picker.PickAt(140, 120, [editor]);

        Assert.NotNull(hit);
        Assert.Equal(PickerTargetKind.DesktopIcon, hit.Kind);
        Assert.Equal(link, hit.ItemPath);
        Assert.Equal(exe, hit.ImagePath, ignoreCase: true);
        Assert.Same(editor, hit.App);
        Assert.Equal(new ScreenRect(100, 105, 176, 190), hit.Bounds);
        Assert.Null(picker.PickAt(140, 20, [editor]));
    }

    [Fact]
    public void Without_the_list_view_the_desktop_falls_back_to_accessibility()
    {
        var surfaces = new FakeSurfaces { Accessible = ("Recycle Bin", new ScreenRect(0, 5, 76, 75)) };
        var picker = new WindowPicker(new FakeProbe(), surfaces, skippedProcessId: 1);
        picker.ReadDesktop();

        PickerHit? hit = picker.PickAt(30, 30, []);

        Assert.Equal("Recycle Bin", hit?.WindowTitle);
        Assert.Null(hit?.ItemPath);
        Assert.Null(hit?.App);
    }

    [Fact]
    public void A_tray_icon_leads_to_the_process_that_owns_it()
    {
        InstalledApp vpn = App("reg:vpn", @"C:\Apps\Vpn");
        var probe = new FakeProbe { Images = { [Taskbar] = @"C:\Windows\explorer.exe", [4100] = @"C:\Apps\Vpn\vpn.exe" } };
        var surfaces = new FakeSurfaces
        {
            Tray = new TraySnapshot([new TrayIcon(0x5150, 4100, new ScreenRect(1600, 1032, 1632, 1080))], [new ScreenRect(1679, 1032, 1711, 1080)]),
        };
        var picker = new WindowPicker(probe, surfaces, skippedProcessId: 1);

        PickerHit? icon = picker.PickAt(1610, 1050, [vpn]);
        PickerHit? bar = picker.PickAt(900, 1050, [vpn]);

        Assert.Equal(PickerTargetKind.TrayIcon, icon?.Kind);
        Assert.Equal(4100, icon?.ProcessId);
        Assert.Same(vpn, icon?.App);
        Assert.Equal(0x5150, icon?.Window);
        Assert.Equal(PickerTargetKind.Window, bar?.Kind);
        Assert.Null(bar?.App);
        Assert.Equal([new ScreenRect(1679, 1032, 1711, 1080)], picker.OverflowButtons);
    }

    [Fact]
    public void Hidden_tray_icons_name_the_overflow_button_until_it_opens()
    {
        var shown = new TrayIcon(1, 100, new ScreenRect(1600, 1032, 1632, 1080));
        var chevron = new ScreenRect(1679, 1032, 1711, 1080);
        (TrayIcon, bool)[] remembered = [(shown, true), (new TrayIcon(2, 200, chevron), false), (new TrayIcon(3, 300, chevron), false)];

        TraySnapshot closed = TrayIcons.Split(remembered, overflowOpen: false);
        TraySnapshot open = TrayIcons.Split([(shown, true), (new TrayIcon(2, 200, new ScreenRect(1500, 900, 1540, 940)), false)], overflowOpen: true);

        Assert.Equal([shown], closed.Icons);
        Assert.Equal([chevron], closed.OverflowButtons);
        Assert.Equal(2, open.Icons.Count);
        Assert.Empty(open.OverflowButtons);
        Assert.Empty(TrayIcons.Distinct([new TrayIcon(2, 200, chevron), new TrayIcon(3, 300, chevron)]));
    }

    [Fact]
    public void Remembered_tray_paths_expand_their_known_folder()
    {
        var programFiles = Guid.Parse("6D809377-6AF0-444B-8957-A3773F02200E");
        string? Folder(Guid id) => id == programFiles ? @"C:\Program Files" : null;

        Assert.Equal(@"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", NotifyIconSetting.ExpandPath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\NVIDIA Corporation\NvContainer\nvcontainer.exe", Folder));
        Assert.Equal(@"C:\ProgramData\Vendor\helper.exe", NotifyIconSetting.ExpandPath(@"C:\ProgramData\Vendor\helper.exe", Folder));
        Assert.Null(NotifyIconSetting.ExpandPath(@"{00000000-0000-0000-0000-000000000001}\x.exe", Folder));
    }

    [Fact]
    public void A_chosen_program_stands_for_itself()
    {
        InstalledApp editor = App("reg:editor", Path.Combine(_scratch.FullName, "Editor"));
        string exe = Program(editor, "editor.exe");

        PickerHit hit = TargetResolver.FromFile(exe, [editor]);

        Assert.Equal(PickerTargetKind.File, hit.Kind);
        Assert.Equal("editor.exe", hit.WindowTitle);
        Assert.Equal(exe, hit.ImagePath);
        Assert.Same(editor, hit.App);
        Assert.Equal(PickerMatch.InstallFolder, hit.MatchedBy);
    }

    [Fact]
    public void A_chosen_shortcut_leads_to_its_target_without_being_resolved()
    {
        InstalledApp editor = App("reg:editor", Path.Combine(_scratch.FullName, "Editor"));
        string exe = Program(editor, "editor.exe");
        string link = Shortcut("Editor.lnk", exe);

        PickerHit hit = TargetResolver.FromFile(link, [editor]);

        Assert.Equal("Editor", hit.WindowTitle);
        Assert.Equal(link, hit.ItemPath);
        Assert.Equal(exe, hit.ImagePath, ignoreCase: true);
        Assert.Same(editor, hit.App);
    }

    [Fact]
    public void A_steam_shortcut_leads_to_the_game()
    {
        InstalledApp game = App("reg:steam", null) with { KeyName = "Steam App 292410", InstallerKind = InstallerKind.Steam };
        string url = Path.Combine(_scratch.FullName, "Street Racing Syndicate.url");
        File.WriteAllLines(url, ["[InternetShortcut]", "IDList=", "URL=steam://rungameid/292410", @"IconFile=C:\Steam\steam\games\abc.ico"]);
        string local = Path.Combine(_scratch.FullName, "page.url");
        File.WriteAllLines(local, ["[InternetShortcut]", $"URL=file:///{Path.Combine(_scratch.FullName, "tool.exe").Replace('\\', '/')}"]);

        PickerHit steam = TargetResolver.FromFile(url, [game]);
        PickerHit file = TargetResolver.FromFile(local, [game]);

        Assert.Equal(292410, steam.SteamAppId);
        Assert.Same(game, steam.App);
        Assert.Equal(PickerMatch.SteamAppId, steam.MatchedBy);
        Assert.Null(steam.ImagePath);
        Assert.Equal(Path.Combine(_scratch.FullName, "tool.exe"), file.ImagePath, ignoreCase: true);
        Assert.Null(file.App);
    }

    [Fact]
    public void Desktop_labels_find_the_user_s_item_first()
    {
        DesktopItem[] items = [new(@"C:\Users\u\Desktop\Steam.lnk", "Steam"), new(@"C:\Users\Public\Desktop\Steam.lnk", "Steam")];

        Assert.Equal(@"C:\Users\u\Desktop\Steam.lnk", TargetResolver.ItemFor("steam", items));
        Assert.Null(TargetResolver.ItemFor("Recycle Bin", items));
        Assert.Null(TargetResolver.ItemFor(string.Empty, items));
    }

    [Fact]
    public void Quick_actions_find_the_running_programs_and_startup_entries_of_a_pick()
    {
        InstalledApp editor = App("reg:editor", @"C:\Apps\Editor");
        var processes = new FakeProcesses(
            new RunningProcess(11, "editor.exe", @"C:\Apps\Editor\editor.exe"),
            new RunningProcess(12, "helper.exe", @"C:\Apps\Editor\bin\helper.exe"),
            new RunningProcess(13, "other.exe", @"C:\Other\other.exe"),
            new RunningProcess(14, "protected", null));
        var actions = new PickerActions(processes);
        var file = new PickerHit(0, "editor.exe", string.Empty, 0, @"C:\Apps\Editor\editor.exe", null, editor, PickerMatch.InstallFolder) { Kind = PickerTargetKind.File };
        var window = new PickerHit(5, "Other", "X", 13, @"C:\Other\other.exe", null, null, PickerMatch.None);
        var run = new StartupLocation(StartupKind.RunValue, InstallScope.User, false, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "approved");
        StartupEntry[] entries =
        [
            new(run, "Editor", "\"C:\\Apps\\Editor\\editor.exe\" /tray", @"C:\Apps\Editor\editor.exe", true, null),
            new(run, "Editor updater", @"C:\Apps\Editor\bin\update.exe", @"C:\Apps\Editor\bin\update.exe", false, DateTimeOffset.Now),
            new(run, "Other", @"C:\Other\other.exe", @"C:\Other\other.exe", true, null),
        ];

        IReadOnlyList<RunningProcess> running = actions.Running(file);
        IReadOnlyList<ProcessStop> stops = actions.Stop(running);

        Assert.Equal([11, 12], running.Select(p => p.Id));
        Assert.Equal([13], actions.Running(window).Select(p => p.Id));
        Assert.Equal([11, 12], processes.Terminated);
        Assert.All(stops, s => Assert.Equal(TerminateOutcome.Terminated, s.Result.Outcome));
        Assert.Equal(["Editor"], PickerActions.StartupEntries(file, entries).Select(e => e.Name));
        Assert.Equal(["Other"], PickerActions.StartupEntries(window, entries).Select(e => e.Name));
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

    private static string Program(InstalledApp app, string name)
    {
        Directory.CreateDirectory(app.InstallLocation!);
        string path = Path.Combine(app.InstallLocation!, name);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    private string Shortcut(string name, string target)
    {
        string path = Path.Combine(_scratch.FullName, name);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Save();
        return path;
    }

    private sealed class FakeProbe : IWindowProbe
    {
        public Dictionary<int, string> Images { get; } = [];

        public IEnumerable<WindowCandidate> ZOrder() =>
        [
            new WindowCandidate(Taskbar, Taskbar, Visible: true, Bounds: new ScreenRect(0, 1032, 1920, 1080)),
            new WindowCandidate(Desktop, Desktop, Visible: true, Bounds: Monitor),
        ];

        public WindowInfo? Describe(nint window) => window switch
        {
            Taskbar => new WindowInfo(Taskbar, Taskbar, "Shell_TrayWnd", string.Empty, new ScreenRect(0, 1032, 1920, 1080)),
            Desktop => new WindowInfo(Desktop, Desktop, "Progman", "Program Manager", Monitor),
            _ => null,
        };

        public string? ImagePath(int processId) => Images.GetValueOrDefault(processId);

        public string? PackageFullName(int processId) => null;
    }

    private sealed class FakeSurfaces : IShellSurfaces
    {
        public DesktopIconView? View { get; init; }

        public (string Label, ScreenRect Bounds)? Accessible { get; init; }

        public IReadOnlyList<DesktopItem> Items { get; init; } = [];

        public TraySnapshot Tray { get; init; } = new([], []);

        public (int X, int Y) ClientOffset { get; init; }

        public DesktopIconView? DesktopIcons() => View;

        public (string Label, ScreenRect Bounds)? AccessibleDesktopIconAt(int x, int y) =>
            Accessible is { } icon && icon.Bounds.Contains(x, y) ? icon : null;

        public IReadOnlyList<DesktopItem> DesktopItems() => Items;

        public TraySnapshot TrayIcons() => Tray;

        public (int X, int Y) ToClient(nint window, int x, int y) => (x - ClientOffset.X, y - ClientOffset.Y);

        public ScreenRect ToScreen(nint window, ScreenRect client) =>
            new(client.Left + ClientOffset.X, client.Top + ClientOffset.Y, client.Right + ClientOffset.X, client.Bottom + ClientOffset.Y);
    }

    private sealed class FakeProcesses(params RunningProcess[] running) : IProcessControl
    {
        public List<int> Terminated { get; } = [];

        public IReadOnlyList<RunningProcess> List() => running;

        public TerminateResult Terminate(RunningProcess process, TimeSpan wait)
        {
            Terminated.Add(process.Id);
            return new TerminateResult(TerminateOutcome.Terminated, 0);
        }
    }
}
