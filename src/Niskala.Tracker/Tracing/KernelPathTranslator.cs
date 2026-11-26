using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;
using Niskala.Interop.FileSystem;

namespace Niskala.Tracker.Tracing;

public sealed class KernelPathTranslator(IReadOnlyDictionary<string, string> driveDevices, string userSid, int currentControlSet = 1)
{
    private const string Registry = @"\REGISTRY\";
    private const string Machine = @"\REGISTRY\MACHINE";
    private const string Users = @"\REGISTRY\USER\";
    private const string ClassesSuffix = "_Classes";
    private const string Mup = @"\Device\Mup\";

    private readonly string _controlSet = string.Create(CultureInfo.InvariantCulture, $@"\SYSTEM\ControlSet{currentControlSet:D3}");
    private readonly KeyValuePair<string, string>[] _devices = [.. driveDevices.OrderByDescending(d => d.Key.Length)];

    public static KernelPathTranslator ForCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using RegistryKey? select = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\Select");
        return new KernelPathTranslator(DeviceNames.DriveDevices(), identity.User!.Value, select?.GetValue("Current") as int? ?? 1);
    }

    public string? File(string kernelPath)
    {
        ArgumentNullException.ThrowIfNull(kernelPath);
        string path = kernelPath.StartsWith(@"\??\", StringComparison.Ordinal) || kernelPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? kernelPath[4..]
            : kernelPath;

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            return path;
        }

        if (path.StartsWith(Mup, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[Mup.Length..];
        }

        foreach ((string device, string drive) in _devices)
        {
            if (path.Length > device.Length && path[device.Length] == '\\' && path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return drive + path[device.Length..];
            }
        }

        return null;
    }

    public string? Key(string kernelPath)
    {
        ArgumentNullException.ThrowIfNull(kernelPath);
        if (!kernelPath.StartsWith(Registry, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Below(kernelPath, Machine) is { } machine)
        {
            return Below(machine, _controlSet) is { } controlSet ? @"HKLM\SYSTEM\CurrentControlSet" + controlSet : "HKLM" + machine;
        }

        if (!kernelPath.StartsWith(Users, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = kernelPath[Users.Length..];
        int slash = rest.IndexOf('\\', StringComparison.Ordinal);
        string hive = slash < 0 ? rest : rest[..slash];
        string below = slash < 0 ? string.Empty : rest[slash..];
        if (hive.Length == 0)
        {
            return null;
        }

        if (string.Equals(hive, userSid, StringComparison.OrdinalIgnoreCase))
        {
            return "HKCU" + below;
        }

        return string.Equals(hive, userSid + ClassesSuffix, StringComparison.OrdinalIgnoreCase)
            ? @"HKCU\Software\Classes" + below
            : @"HKU\" + hive + below;
    }

    private static string? Below(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '\\')
            ? path[root.Length..]
            : null;
}
