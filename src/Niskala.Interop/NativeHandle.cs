using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Niskala.Interop;

internal static class NativeHandle
{
    public const uint FileReadAttributes = 0x0080;

    public const FILE_SHARE_MODE ShareAll =
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    public static unsafe bool IsInvalid(HANDLE handle) => handle.IsNull || (nint)handle.Value == -1;

    public static unsafe int TryOpen(string path, uint access, FILE_FLAGS_AND_ATTRIBUTES flags, out HANDLE handle)
    {
        fixed (char* p = path)
        {
            handle = PInvoke.CreateFile(p, access, ShareAll, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, flags, HANDLE.Null);
        }

        return IsInvalid(handle) ? Marshal.GetLastPInvokeError() : 0;
    }
}
