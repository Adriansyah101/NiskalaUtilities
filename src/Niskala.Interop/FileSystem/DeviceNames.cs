using Windows.Win32;

namespace Niskala.Interop.FileSystem;

public static class DeviceNames
{
    private const int TargetCapacity = 1024;

    public static Dictionary<string, string> DriveDevices()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char[] target = new char[TargetCapacity];
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = $"{letter}:";
            uint length = PInvoke.QueryDosDevice(drive, target);
            if (length == 0)
            {
                continue;
            }

            int end = Array.IndexOf(target, '\0', 0, (int)length);
            string device = new(target, 0, end < 0 ? (int)length : end);
            if (device.Length > 0)
            {
                devices.TryAdd(device, drive);
            }
        }

        return devices;
    }
}
