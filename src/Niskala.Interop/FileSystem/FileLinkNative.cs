using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Niskala.Interop.FileSystem;

public readonly record struct FileLinkInfo(uint VolumeSerial, ulong FileIndex, int Links);

public static partial class FileLinkNative
{
    public static unsafe int TryGet(SafeFileHandle handle, out FileLinkInfo info)
    {
        ArgumentNullException.ThrowIfNull(handle);
        info = default;
        HandleFileInformation data;
        if (!GetFileInformationByHandle(handle, &data))
        {
            return Marshal.GetLastPInvokeError();
        }

        info = new FileLinkInfo(data.VolumeSerialNumber, ((ulong)data.FileIndexHigh << 32) | data.FileIndexLow, (int)data.NumberOfLinks);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle file, HandleFileInformation* information);
}
