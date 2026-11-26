using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Niskala.Interop.Shell;

public static class FileDialogNative
{
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public static string? PickFile(nint owner, string title, IReadOnlyList<(string Name, string Pattern)> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return Pick(owner, title, filters, FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
    }

    public static string? PickFileOrShortcut(nint owner, string title, IReadOnlyList<(string Name, string Pattern)> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return Pick(owner, title, filters, FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_NODEREFERENCELINKS);
    }

    public static string? PickFolder(nint owner, string title) => Pick(owner, title, [], FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS);

    private static unsafe string? Pick(nint owner, string title, IReadOnlyList<(string Name, string Pattern)> filters, FILEOPENDIALOGOPTIONS options)
    {
        ArgumentNullException.ThrowIfNull(title);
        IFileOpenDialog* dialog = null;
        IShellItem* item = null;
        var pinned = new List<GCHandle>();
        try
        {
            Marshal.ThrowExceptionForHR(FileOpenDialog.CreateInstance(out dialog));
            dialog->GetOptions(out FILEOPENDIALOGOPTIONS current);
            dialog->SetOptions(current | options | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR);
            fixed (char* text = title)
            {
                dialog->SetTitle(text);
            }

            if (filters.Count > 0)
            {
                var specs = new COMDLG_FILTERSPEC[filters.Count];
                for (int i = 0; i < filters.Count; i++)
                {
                    GCHandle name = GCHandle.Alloc(filters[i].Name, GCHandleType.Pinned);
                    GCHandle pattern = GCHandle.Alloc(filters[i].Pattern, GCHandleType.Pinned);
                    pinned.Add(name);
                    pinned.Add(pattern);
                    specs[i] = new COMDLG_FILTERSPEC { pszName = (char*)name.AddrOfPinnedObject(), pszSpec = (char*)pattern.AddrOfPinnedObject() };
                }

                fixed (COMDLG_FILTERSPEC* spec = specs)
                {
                    dialog->SetFileTypes((uint)specs.Length, spec);
                }
            }

            try
            {
                dialog->Show(new HWND(owner));
            }
            catch (COMException ex) when (ex.HResult == ErrorCancelled)
            {
                return null;
            }

            dialog->GetResult(&item);
            PWSTR path;
            item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path);
            try
            {
                return path.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(path);
            }
        }
        finally
        {
            foreach (GCHandle handle in pinned)
            {
                handle.Free();
            }

            if (item is not null)
            {
                item->Release();
            }

            if (dialog is not null)
            {
                dialog->Release();
            }
        }
    }
}
