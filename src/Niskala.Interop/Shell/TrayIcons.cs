using System.Runtime.InteropServices;
using Microsoft.Win32;
using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Niskala.Interop.Shell;

public readonly record struct TrayIcon(nint Owner, int ProcessId, ScreenRect Bounds);

public sealed record TraySnapshot(IReadOnlyList<TrayIcon> Icons, IReadOnlyList<ScreenRect> OverflowButtons);

public sealed record NotifyIconSetting(string ExecutablePath, uint? Id, Guid? Item, bool Promoted)
{
    public static string? ExpandPath(string stored, Func<Guid, string?> knownFolder)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(knownFolder);
        int close = stored.IndexOf('}', StringComparison.Ordinal);
        if (!stored.StartsWith('{') || close < 0)
        {
            return stored.Length == 0 ? null : stored;
        }

        return Guid.TryParse(stored.AsSpan(0, close + 1), out Guid folder) && knownFolder(folder) is { } root
            ? Path.Join(root, stored.AsSpan(close + 1).TrimStart('\\'))
            : null;
    }
}

public static partial class TrayIcons
{
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string OverflowClass = "NotifyIconOverflowWindow";
    private const string OverflowIslandClass = "TopLevelWindowForOverflowXamlIsland";
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";

    private static readonly string[] TrayClasses = [TaskbarClass, "Shell_SecondaryTrayWnd", OverflowClass, OverflowIslandClass];

    public static bool IsTrayClass(string className) => TrayClasses.Contains(className, StringComparer.Ordinal);

    public static TraySnapshot Snapshot()
    {
        List<TrayIcon> icons = ToolbarIcons();
        if (icons.Count > 0)
        {
            return new TraySnapshot(Distinct(icons), []);
        }

        bool overflowOpen = OverflowOpen();
        List<(TrayIcon Icon, bool Promoted)> remembered = RememberedIcons(Settings());
        return Split(remembered, overflowOpen);
    }

    public static TraySnapshot Split(IReadOnlyList<(TrayIcon Icon, bool Promoted)> remembered, bool overflowOpen)
    {
        ArgumentNullException.ThrowIfNull(remembered);
        if (overflowOpen)
        {
            return new TraySnapshot(Distinct(remembered.Select(r => r.Icon)), []);
        }

        return new TraySnapshot(
            Distinct(remembered.Where(r => r.Promoted).Select(r => r.Icon)),
            [.. remembered.Where(r => !r.Promoted && r.Icon.Bounds.Width > 0).Select(r => r.Icon.Bounds).Distinct()]);
    }

    public static IReadOnlyList<TrayIcon> Distinct(IEnumerable<TrayIcon> icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        return [.. icons
            .Where(i => i.Bounds.Width > 0 && i.Bounds.Height > 0)
            .GroupBy(i => i.Bounds)
            .Where(g => g.Select(i => i.ProcessId).Distinct().Count() == 1)
            .Select(g => g.First())];
    }

    public static IReadOnlyList<NotifyIconSetting> Settings()
    {
        using RegistryKey? root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey);
        if (root is null)
        {
            return [];
        }

        var settings = new List<NotifyIconSetting>();
        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? entry = root.OpenSubKey(name);
            if (entry?.GetValue("ExecutablePath") is not string stored || NotifyIconSetting.ExpandPath(stored, KnownFolder) is not { } path)
            {
                continue;
            }

            uint? id = entry.GetValue("UID") is int uid ? unchecked((uint)uid) : null;
            Guid? item = entry.GetValue("IconGuid") is string text && Guid.TryParse(text, out Guid guid) ? guid : null;
            if (id is null && item is null)
            {
                continue;
            }

            settings.Add(new NotifyIconSetting(path, id, item, entry.GetValue("IsPromoted") is int promoted && promoted != 0));
        }

        return settings;
    }

    private static List<(TrayIcon Icon, bool Promoted)> RememberedIcons(IReadOnlyList<NotifyIconSetting> settings)
    {
        var icons = new List<(TrayIcon, bool)>();
        if (settings.Count == 0 || ProcessNative.TrySnapshot(out List<ProcessEntry> processes) != ProcessNative.ErrorSuccess)
        {
            return icons;
        }

        foreach (ProcessEntry process in processes)
        {
            List<NotifyIconSetting> candidates = [.. settings.Where(s => Path.GetFileName(s.ExecutablePath).Equals(process.ExeName, StringComparison.OrdinalIgnoreCase))];
            if (candidates.Count == 0 || ProcessNative.TryGetImagePath(process.Id) is not { } image)
            {
                continue;
            }

            candidates.RemoveAll(s => !s.ExecutablePath.Equals(image, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<nint> windows = candidates.Count == 0 ? [] : WindowNative.ProcessWindows(process.Id);
            foreach (NotifyIconSetting setting in candidates)
            {
                foreach (nint window in windows)
                {
                    if (RectOf(window, setting) is { } bounds)
                    {
                        icons.Add((new TrayIcon(window, process.Id, bounds), setting.Promoted));
                        break;
                    }
                }
            }
        }

        return icons;
    }

    private static unsafe ScreenRect? RectOf(nint window, NotifyIconSetting setting)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)sizeof(NotifyIconIdentifier),
            Window = window,
            Id = setting.Id ?? 0,
            Item = setting.Item ?? Guid.Empty,
        };
        RECT rect;
        return ShellNotifyIconGetRect(&identifier, &rect) == 0 ? new ScreenRect(rect.left, rect.top, rect.right, rect.bottom) : null;
    }

    private static unsafe List<TrayIcon> ToolbarIcons()
    {
        var icons = new List<TrayIcon>();
        foreach (nint toolbar in Toolbars())
        {
            if (WindowNative.Send(toolbar, PInvoke.TB_BUTTONCOUNT, 0, 0) is not { } count)
            {
                continue;
            }

            PInvoke.GetWindowThreadProcessId(new HWND(toolbar), out uint explorer);
            using RemoteMemory? remote = RemoteMemory.Open((int)explorer, sizeof(TrayButtonX64) + sizeof(RECT));
            if (remote is null)
            {
                continue;
            }

            for (int i = 0; i < (int)count; i++)
            {
                if (WindowNative.Send(toolbar, PInvoke.TB_GETBUTTON, (nuint)i, remote.Address) is not (> 0)
                    || !remote.Read(0, out TrayButtonX64 button)
                    || (button.State & PInvoke.TBSTATE_HIDDEN) != 0
                    || !remote.ReadAt(button.Data, out TrayData data)
                    || WindowNative.Send(toolbar, PInvoke.TB_GETITEMRECT, (nuint)i, remote.Address + sizeof(TrayButtonX64)) is not (> 0)
                    || !remote.Read(sizeof(TrayButtonX64), out RECT rect))
                {
                    continue;
                }

                PInvoke.GetWindowThreadProcessId(new HWND(data.Window), out uint owner);
                if (owner != 0)
                {
                    icons.Add(new TrayIcon(data.Window, (int)owner, WindowNative.ToScreen(toolbar, new ScreenRect(rect.left, rect.top, rect.right, rect.bottom))));
                }
            }
        }

        return icons;
    }

    private static IEnumerable<nint> Toolbars()
    {
        HWND taskbar = PInvoke.FindWindowEx(HWND.Null, HWND.Null, TaskbarClass, null);
        HWND notify = taskbar.IsNull ? HWND.Null : PInvoke.FindWindowEx(taskbar, HWND.Null, "TrayNotifyWnd", null);
        HWND pager = notify.IsNull ? HWND.Null : PInvoke.FindWindowEx(notify, HWND.Null, "SysPager", null);
        HWND shown = pager.IsNull ? HWND.Null : PInvoke.FindWindowEx(pager, HWND.Null, "ToolbarWindow32", null);
        if (!shown.IsNull)
        {
            yield return shown;
        }

        HWND overflow = PInvoke.FindWindowEx(HWND.Null, HWND.Null, OverflowClass, null);
        HWND hidden = overflow.IsNull ? HWND.Null : PInvoke.FindWindowEx(overflow, HWND.Null, "ToolbarWindow32", null);
        if (!hidden.IsNull && PInvoke.IsWindowVisible(overflow))
        {
            yield return hidden;
        }
    }

    private static bool OverflowOpen()
    {
        HWND island = PInvoke.FindWindowEx(HWND.Null, HWND.Null, OverflowIslandClass, null);
        return !island.IsNull && PInvoke.IsWindowVisible(island);
    }

    private static unsafe string? KnownFolder(Guid folder)
    {
        PWSTR path = default;
        try
        {
            return PInvoke.SHGetKnownFolderPath(&folder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, HANDLE.Null, &path).Succeeded ? path.ToString() : null;
        }
        finally
        {
            if (path.Value is not null)
            {
                PInvoke.CoTaskMemFree(path);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TrayButtonX64
    {
        public int Bitmap;
        public int Command;
        public byte State;
        public byte Style;
        public fixed byte Reserved[6];
        public nint Data;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayData
    {
        public nint Window;
        public uint Id;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static unsafe partial int ShellNotifyIconGetRect(NotifyIconIdentifier* identifier, global::Windows.Win32.Foundation.RECT* rect);
}
