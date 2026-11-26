using System.Runtime.InteropServices;
using Niskala.Interop.Com;
using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;

namespace Niskala.Interop.Shell;

public readonly record struct DesktopIconSlot(int Index, string Label, ScreenRect Bounds);

public sealed record DesktopIconView(nint ListView, IReadOnlyList<DesktopIconSlot> Icons)
{
    public DesktopIconSlot? At(int x, int y)
    {
        foreach (DesktopIconSlot slot in Icons)
        {
            if (slot.Bounds.Contains(x, y))
            {
                return slot;
            }
        }

        return null;
    }
}

public readonly record struct DesktopItem(string Path, string Label);

public static class DesktopIcons
{
    private const string ProgmanClass = "Progman";
    private const string WorkerClass = "WorkerW";
    private const string DefaultViewClass = "SHELLDLL_DefView";
    private const string ListViewClass = "SysListView32";
    private const int LabelCapacity = 260;
    private const int ChildIdSelf = 0;

    public static bool IsDesktopClass(string className) => className is ProgmanClass or WorkerClass;

    public static nint FindListView()
    {
        foreach (HWND host in Hosts())
        {
            HWND view = PInvoke.FindWindowEx(host, HWND.Null, DefaultViewClass, null);
            HWND list = view.IsNull ? HWND.Null : PInvoke.FindWindowEx(view, HWND.Null, ListViewClass, null);
            if (!list.IsNull)
            {
                return list;
            }
        }

        return 0;
    }

    public static unsafe DesktopIconView? Read(nint listView)
    {
        if (WindowNative.Send(listView, PInvoke.LVM_GETITEMCOUNT, 0, 0) is not { } answer)
        {
            return null;
        }

        int count = (int)answer;
        PInvoke.GetWindowThreadProcessId(new HWND(listView), out uint owner);
        int textOffset = sizeof(LVITEMW);
        using RemoteMemory? remote = RemoteMemory.Open((int)owner, textOffset + (LabelCapacity * sizeof(char)));
        if (remote is null)
        {
            return null;
        }

        var icons = new List<DesktopIconSlot>(count);
        for (int i = 0; i < count; i++)
        {
            if (!remote.Write(0, new RECT { left = (int)PInvoke.LVIR_BOUNDS })
                || WindowNative.Send(listView, PInvoke.LVM_GETITEMRECT, (nuint)i, remote.Address) is not (> 0)
                || !remote.Read(0, out RECT bounds))
            {
                continue;
            }

            var item = new LVITEMW
            {
                mask = LIST_VIEW_ITEM_FLAGS.LVIF_TEXT,
                iItem = i,
                pszText = (char*)(remote.Address + textOffset),
                cchTextMax = LabelCapacity,
            };
            string? label = remote.Write(0, item) && WindowNative.Send(listView, PInvoke.LVM_GETITEMTEXTW, (nuint)i, remote.Address) is not null
                ? remote.ReadString(textOffset, LabelCapacity)
                : null;
            icons.Add(new DesktopIconSlot(i, label ?? string.Empty, new ScreenRect(bounds.left, bounds.top, bounds.right, bounds.bottom)));
        }

        return new DesktopIconView(listView, icons);
    }

    public static unsafe (string Label, ScreenRect Bounds)? AccessibleAt(nint listView, int x, int y)
    {
        using ComScope com = ComScope.Enter();
        IAccessible* accessible = null;
        VARIANT child = default;
        try
        {
            Guid id = IAccessible.IID_Guid;
            if (PInvoke.AccessibleObjectFromWindow(new HWND(listView), unchecked((uint)Windows.Win32.UI.WindowsAndMessaging.OBJECT_IDENTIFIER.OBJID_CLIENT), &id, (void**)&accessible).Failed)
            {
                return null;
            }

            child = accessible->accHitTest(x, y);
            if (child.vt != VARENUM.VT_I4 || child.lVal == ChildIdSelf)
            {
                return null;
            }

            string label = BstrText.Take(accessible->get_accName(child));
            accessible->accLocation(out int left, out int top, out int width, out int height, child);
            return (label, new ScreenRect(left, top, left + width, top + height));
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            PInvoke.VariantClear(&child);
            if (accessible is not null)
            {
                accessible->Release();
            }
        }
    }

    public static IReadOnlyList<DesktopItem> Items()
    {
        using ComScope com = ComScope.Enter();
        var items = new List<DesktopItem>();
        foreach (Environment.SpecialFolder folder in (ReadOnlySpan<Environment.SpecialFolder>)[Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory])
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0 || !Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                if (DisplayName(path) is { } label)
                {
                    items.Add(new DesktopItem(path, label));
                }
            }
        }

        return items;
    }

    private static IEnumerable<HWND> Hosts()
    {
        HWND progman = PInvoke.FindWindowEx(HWND.Null, HWND.Null, ProgmanClass, null);
        if (!progman.IsNull)
        {
            yield return progman;
            for (HWND worker = PInvoke.FindWindowEx(progman, HWND.Null, WorkerClass, null); !worker.IsNull; worker = PInvoke.FindWindowEx(progman, worker, WorkerClass, null))
            {
                yield return worker;
            }
        }

        for (HWND worker = PInvoke.FindWindowEx(HWND.Null, HWND.Null, WorkerClass, null); !worker.IsNull; worker = PInvoke.FindWindowEx(HWND.Null, worker, WorkerClass, null))
        {
            yield return worker;
        }
    }

    private static unsafe string? DisplayName(string path)
    {
        IShellItem* item = null;
        PWSTR name = default;
        try
        {
            Guid id = IShellItem.IID_Guid;
            fixed (char* p = path)
            {
                if (PInvoke.SHCreateItemFromParsingName(p, null, &id, (void**)&item).Failed)
                {
                    return null;
                }
            }

            item->GetDisplayName(SIGDN.SIGDN_NORMALDISPLAY, &name);
            return name.ToString();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (name.Value is not null)
            {
                PInvoke.CoTaskMemFree(name);
            }

            if (item is not null)
            {
                item->Release();
            }
        }
    }
}
