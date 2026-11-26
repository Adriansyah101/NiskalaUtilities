using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Niskala.Interop.Storage;

public static class StorageNative
{
    public static int TryGetVolumeDevice(string path, out string device)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        device = string.Empty;

        Span<char> mountPoint = stackalloc char[path.Length + 2];
        if (!PInvoke.GetVolumePathName(path, mountPoint))
        {
            return Marshal.GetLastPInvokeError();
        }

        Span<char> volume = stackalloc char[64];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(TrimAtNull(mountPoint), volume))
        {
            return Marshal.GetLastPInvokeError();
        }

        device = TrimAtNull(volume).TrimEnd('\\');
        return 0;
    }

    public static unsafe int TryQuerySeekPenalty(string volumeDevice, out bool incursSeekPenalty)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeDevice);
        incursSeekPenalty = false;

        int error = NativeHandle.TryOpen(volumeDevice, 0, default(FILE_FLAGS_AND_ATTRIBUTES), out HANDLE handle);
        if (error != 0)
        {
            return error;
        }

        try
        {
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty,
                QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
            };
            DEVICE_SEEK_PENALTY_DESCRIPTOR descriptor;
            uint returned;
            if (!PInvoke.DeviceIoControl(
                handle,
                PInvoke.IOCTL_STORAGE_QUERY_PROPERTY,
                &query,
                (uint)sizeof(STORAGE_PROPERTY_QUERY),
                &descriptor,
                (uint)sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR),
                &returned,
                null))
            {
                return Marshal.GetLastPInvokeError();
            }

            incursSeekPenalty = descriptor.IncursSeekPenalty;
            return 0;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static string TrimAtNull(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');
        return new string(end < 0 ? buffer : buffer[..end]);
    }
}
