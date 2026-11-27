using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Niskala.Interop.Shell;

public static class KnownFolders
{
    public static Guid Downloads { get; } = new("374DE290-123F-4565-9164-39C4925E467B");

    public static unsafe string? Find(Guid folder)
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
}
