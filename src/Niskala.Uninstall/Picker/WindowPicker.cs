using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;
using Niskala.Interop.Shell;

namespace Niskala.Uninstall.Picker;

public enum PickerMatch
{
    None,
    Package,
    InstallFolder,
    DisplayIcon,
    ShortcutProduct,
    SteamAppId,
}

public enum PickerTargetKind
{
    Window,
    DesktopIcon,
    TrayIcon,
    File,
}

public sealed record PickerHit(
    long Window,
    string WindowTitle,
    string WindowClass,
    int ProcessId,
    string? ImagePath,
    string? PackageFullName,
    InstalledApp? App,
    PickerMatch MatchedBy)
{
    public PickerTargetKind Kind { get; init; }

    public ScreenRect Bounds { get; init; }

    public string? ItemPath { get; init; }

    public int? SteamAppId { get; init; }
}

public interface IWindowProbe
{
    IEnumerable<WindowCandidate> ZOrder();

    WindowInfo? Describe(nint window);

    string? ImagePath(int processId);

    string? PackageFullName(int processId);
}

public sealed class SystemWindowProbe : IWindowProbe
{
    public static SystemWindowProbe Instance { get; } = new();

    public IEnumerable<WindowCandidate> ZOrder() => WindowNative.ZOrder();

    public WindowInfo? Describe(nint window) => WindowNative.Describe(window);

    public string? ImagePath(int processId) => ProcessNative.TryGetImagePath(processId);

    public string? PackageFullName(int processId) => ProcessNative.TryGetPackageFullName(processId);
}

public interface IShellSurfaces
{
    DesktopIconView? DesktopIcons();

    (string Label, ScreenRect Bounds)? AccessibleDesktopIconAt(int x, int y);

    IReadOnlyList<DesktopItem> DesktopItems();

    TraySnapshot TrayIcons();

    (int X, int Y) ToClient(nint window, int x, int y);

    ScreenRect ToScreen(nint window, ScreenRect client);
}

public sealed class SystemShellSurfaces : IShellSurfaces
{
    public static SystemShellSurfaces Instance { get; } = new();

    public DesktopIconView? DesktopIcons() =>
        Interop.Shell.DesktopIcons.FindListView() is not 0 and var list ? Interop.Shell.DesktopIcons.Read(list) : null;

    public (string Label, ScreenRect Bounds)? AccessibleDesktopIconAt(int x, int y) =>
        Interop.Shell.DesktopIcons.FindListView() is not 0 and var list ? Interop.Shell.DesktopIcons.AccessibleAt(list, x, y) : null;

    public IReadOnlyList<DesktopItem> DesktopItems() => Interop.Shell.DesktopIcons.Items();

    public TraySnapshot TrayIcons() => Interop.Shell.TrayIcons.Snapshot();

    public (int X, int Y) ToClient(nint window, int x, int y) => WindowNative.ToClient(window, x, y);

    public ScreenRect ToScreen(nint window, ScreenRect client) => WindowNative.ToScreen(window, client);
}

public sealed class WindowPicker(IWindowProbe probe, IShellSurfaces surfaces, int skippedProcessId)
{
    private DesktopIconView? _desktop;
    private IReadOnlyList<DesktopItem> _items = [];
    private TraySnapshot? _tray;
    private string? _trayClass;

    public WindowPicker(IWindowProbe probe, int skippedProcessId)
        : this(probe, SystemShellSurfaces.Instance, skippedProcessId)
    {
    }

    public WindowPicker(IWindowProbe probe)
        : this(probe, SystemShellSurfaces.Instance, Environment.ProcessId)
    {
    }

    public void ReadDesktop()
    {
        _desktop = surfaces.DesktopIcons();
        _items = surfaces.DesktopItems();
    }

    public IReadOnlyList<ScreenRect> OverflowButtons => _tray?.OverflowButtons ?? [];

    public WindowInfo? WindowAt(int x, int y) =>
        ZOrderHitTest.Pick(probe.ZOrder(), x, y, skippedProcessId) is { } window ? probe.Describe(window) : null;

    public PickerHit? PickAt(int x, int y, IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        WindowInfo? window = WindowAt(x, y);
        if (window is { } tray && TrayIcons.IsTrayClass(tray.ClassName))
        {
            if (_tray is null || _trayClass != tray.ClassName)
            {
                _tray = surfaces.TrayIcons();
                _trayClass = tray.ClassName;
            }

            return _tray.Icons.FirstOrDefault(i => i.Bounds.Contains(x, y)) is { Owner: not 0 } icon
                ? Identify(new WindowInfo(icon.Owner, icon.ProcessId, tray.ClassName, string.Empty, icon.Bounds), apps) with { Kind = PickerTargetKind.TrayIcon }
                : Identify(tray, apps);
        }

        _trayClass = null;
        return window is { } shown && !DesktopIcons.IsDesktopClass(shown.ClassName) ? Identify(shown, apps) : DesktopIconAt(x, y, apps);
    }

    public PickerHit Identify(WindowInfo window, IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        string? image = probe.ImagePath(window.ProcessId);
        string? package = probe.PackageFullName(window.ProcessId);
        (InstalledApp? app, PickerMatch match) = Match(image, package, apps);
        return new PickerHit(window.Handle.ToInt64(), window.Title, window.ClassName, window.ProcessId, image, package, app, match) { Bounds = window.Bounds };
    }

    public static (InstalledApp? App, PickerMatch MatchedBy) Match(string? imagePath, string? packageFullName, IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        IEnumerable<InstalledApp> ordered = apps.OrderByDescending(a => a.IsVisible);
        if (packageFullName is not null && Family(packageFullName) is { } family
            && ordered.FirstOrDefault(a => a.Package is { } p && (p.FullName.Equals(packageFullName, StringComparison.OrdinalIgnoreCase) || p.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))) is { } packaged)
        {
            return (packaged, PickerMatch.Package);
        }

        if (imagePath is null || PathSyntax.TryNormalize(imagePath, out string image) != PathVerdict.Allowed)
        {
            return (null, PickerMatch.None);
        }

        InstalledApp? owner = ordered
            .Where(a => a.InstallLocationGuard == PathVerdict.Allowed
                && PathSyntax.TryNormalize(a.InstallLocation, out string root) == PathVerdict.Allowed
                && PathSyntax.IsSameOrUnder(image, root, out _))
            .MaxBy(a => a.InstallLocation!.Length);
        if (owner is not null)
        {
            return (owner, PickerMatch.InstallFolder);
        }

        InstalledApp? icon = ordered.FirstOrDefault(a => IconFile(a.DisplayIcon) is { } file && file.Equals(image, StringComparison.OrdinalIgnoreCase));
        return icon is null ? (null, PickerMatch.None) : (icon, PickerMatch.DisplayIcon);
    }

    private PickerHit? DesktopIconAt(int x, int y, IReadOnlyList<InstalledApp> apps)
    {
        (string Label, ScreenRect Bounds)? icon = null;
        if (_desktop is { } view)
        {
            (int clientX, int clientY) = surfaces.ToClient(view.ListView, x, y);
            if (view.At(clientX, clientY) is { } slot)
            {
                icon = (slot.Label, surfaces.ToScreen(view.ListView, slot.Bounds));
            }
        }
        else
        {
            icon = surfaces.AccessibleDesktopIconAt(x, y);
        }

        if (icon is not { } found)
        {
            return null;
        }

        return TargetResolver.ItemFor(found.Label, _items) is { } item
            ? TargetResolver.FromItem(item, found.Label, found.Bounds, PickerTargetKind.DesktopIcon, apps)
            : new PickerHit(0, found.Label, string.Empty, 0, null, null, null, PickerMatch.None) { Kind = PickerTargetKind.DesktopIcon, Bounds = found.Bounds };
    }

    private static string? Family(string fullName)
    {
        return fullName.Split('_') is [string name, _, _, _, string publisherId] ? $"{name}_{publisherId}" : null;
    }

    private static string? IconFile(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        string text = displayIcon.Trim();
        int comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text.AsSpan(comma + 1), out _))
        {
            text = text[..comma];
        }

        return PathSyntax.TryNormalize(text.Trim().Trim('"'), out string normalized) == PathVerdict.Allowed ? normalized : null;
    }
}
