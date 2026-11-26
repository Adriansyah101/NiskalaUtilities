using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using WdkPInvoke = Windows.Wdk.PInvoke;

namespace Niskala.Interop.Registry;

public sealed record RawRegistryValue(string Name, int Kind, byte[] Data);

public static class RegistryNative
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;

    private const int MaxKeyNameLength = 255;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const uint DeleteAccess = 0x0001_0000;
    private const string LinkValueName = "SymbolicLinkValue";

    public static SafeRegistryHandle Hive(Microsoft.Win32.RegistryHive hive) => new((nint)(int)hive, ownsHandle: false);

    public static int TryOpenNoFollow(SafeRegistryHandle parent, string subKey, bool registry32, bool writable, out SafeRegistryHandle key)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrEmpty(subKey);
        REG_SAM_FLAGS access = REG_SAM_FLAGS.KEY_READ | View(registry32);
        if (writable)
        {
            access |= (REG_SAM_FLAGS)DeleteAccess | REG_SAM_FLAGS.KEY_SET_VALUE | REG_SAM_FLAGS.KEY_CREATE_SUB_KEY;
        }

        return (int)PInvoke.RegOpenKeyEx(parent, subKey, (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK, access, out key);
    }

    public static bool IsLink(SafeRegistryHandle key)
    {
        ArgumentNullException.ThrowIfNull(key);
        uint size = 0;
        return PInvoke.RegQueryValueEx(key, LinkValueName, out REG_VALUE_TYPE type, default, ref size) == WIN32_ERROR.ERROR_SUCCESS
            && type == REG_VALUE_TYPE.REG_LINK;
    }

    public static int TryListSubKeys(SafeRegistryHandle key, out List<string> names)
    {
        ArgumentNullException.ThrowIfNull(key);
        names = [];
        Span<char> buffer = stackalloc char[MaxKeyNameLength + 1];
        for (uint index = 0; ; index++)
        {
            uint length = (uint)buffer.Length;
            WIN32_ERROR error = PInvoke.RegEnumKeyEx(key, index, buffer, ref length, default);
            if ((int)error == ErrorNoMoreItems)
            {
                return 0;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return (int)error;
            }

            names.Add(new string(buffer[..(int)length]));
        }
    }

    public static unsafe int TryReadValues(SafeRegistryHandle key, out List<RawRegistryValue> values)
    {
        ArgumentNullException.ThrowIfNull(key);
        values = [];
        uint maxName;
        uint maxData;
        bool added = false;
        try
        {
            key.DangerousAddRef(ref added);
            WIN32_ERROR info = PInvoke.RegQueryInfoKey((HKEY)key.DangerousGetHandle(), default, null, null, null, null, null, null, &maxName, &maxData, null, null);
            if (info != WIN32_ERROR.ERROR_SUCCESS)
            {
                return (int)info;
            }
        }
        finally
        {
            if (added)
            {
                key.DangerousRelease();
            }
        }

        char[] name = new char[maxName + 1];
        byte[] data = new byte[Math.Max(maxData, 1)];
        for (uint index = 0; ; )
        {
            uint nameLength = (uint)name.Length;
            uint dataLength = (uint)data.Length;
            WIN32_ERROR error = PInvoke.RegEnumValue(key, index, name, ref nameLength, out uint type, data, ref dataLength);
            if ((int)error == ErrorNoMoreItems)
            {
                return 0;
            }

            if ((int)error == ErrorMoreData)
            {
                name = new char[Math.Max(name.Length * 2, short.MaxValue)];
                data = new byte[Math.Max(data.Length * 2, (int)dataLength)];
                continue;
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return (int)error;
            }

            values.Add(new RawRegistryValue(new string(name, 0, (int)nameLength), (int)type, data.AsSpan(0, (int)dataLength).ToArray()));
            index++;
        }
    }

    public static int TryReadValue(SafeRegistryHandle key, string name, out RawRegistryValue? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        value = null;
        uint size = 0;
        WIN32_ERROR error = PInvoke.RegQueryValueEx(key, name, out REG_VALUE_TYPE _, default, ref size);
        while (error is WIN32_ERROR.ERROR_SUCCESS or (WIN32_ERROR)ErrorMoreData)
        {
            byte[] data = new byte[size];
            error = PInvoke.RegQueryValueEx(key, name, out REG_VALUE_TYPE type, data, ref size);
            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                value = new RawRegistryValue(name, (int)type, data.AsSpan(0, (int)size).ToArray());
                return 0;
            }
        }

        return (int)error;
    }

    public static int TrySetValue(SafeRegistryHandle key, RawRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return (int)PInvoke.RegSetValueEx(key, value.Name, (REG_VALUE_TYPE)value.Kind, value.Data);
    }

    public static int TryDeleteValue(SafeRegistryHandle key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        return (int)PInvoke.RegDeleteValue(key, name);
    }

    public static int TryDeleteKey(SafeRegistryHandle key)
    {
        ArgumentNullException.ThrowIfNull(key);
        NTSTATUS status = WdkPInvoke.NtDeleteKey(key);
        return status.SeverityCode == NTSTATUS.Severity.Error ? (int)PInvoke.RtlNtStatusToDosError(status) : 0;
    }

    public static int TryCreateKey(SafeRegistryHandle parent, string subKey, bool registry32, out SafeRegistryHandle key)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrEmpty(subKey);
        return (int)PInvoke.RegCreateKeyEx(
            parent,
            subKey,
            default,
            REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE,
            REG_SAM_FLAGS.KEY_READ | REG_SAM_FLAGS.KEY_WRITE | View(registry32),
            null,
            out key);
    }

    public static unsafe int TryGetLastWriteTime(SafeRegistryHandle key, out DateTime lastWriteUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        lastWriteUtc = default;

        bool added = false;
        try
        {
            key.DangerousAddRef(ref added);
            FILETIME fileTime;
            WIN32_ERROR error = PInvoke.RegQueryInfoKey(
                (HKEY)key.DangerousGetHandle(), default, null, null, null, null, null, null, null, null, null, &fileTime);
            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return (int)error;
            }

            long ticks = ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
            lastWriteUtc = DateTime.FromFileTimeUtc(ticks);
            return 0;
        }
        finally
        {
            if (added)
            {
                key.DangerousRelease();
            }
        }
    }

    public static unsafe string? TryReadString(SafeRegistryHandle key, string subKey, string? valueName)
    {
        ArgumentNullException.ThrowIfNull(key);
        const REG_ROUTINE_FLAGS Flags = REG_ROUTINE_FLAGS.RRF_RT_REG_SZ | REG_ROUTINE_FLAGS.RRF_RT_REG_EXPAND_SZ | REG_ROUTINE_FLAGS.RRF_NOEXPAND;
        bool added = false;
        try
        {
            key.DangerousAddRef(ref added);
            var handle = (HKEY)key.DangerousGetHandle();
            char* small = stackalloc char[260];
            uint bytes = 260 * sizeof(char);
            fixed (char* sub = subKey)
            fixed (char* name = valueName)
            {
                WIN32_ERROR error = PInvoke.RegGetValue(handle, sub, name, Flags, null, small, &bytes);
                if (error == WIN32_ERROR.ERROR_SUCCESS)
                {
                    return Text(small, bytes);
                }

                if (error != WIN32_ERROR.ERROR_MORE_DATA)
                {
                    return null;
                }

                char[] large = new char[(bytes / sizeof(char)) + 1];
                fixed (char* buffer = large)
                {
                    bytes = (uint)(large.Length * sizeof(char));
                    return PInvoke.RegGetValue(handle, sub, name, Flags, null, buffer, &bytes) == WIN32_ERROR.ERROR_SUCCESS ? Text(buffer, bytes) : null;
                }
            }
        }
        finally
        {
            if (added)
            {
                key.DangerousRelease();
            }
        }
    }

    private static unsafe string Text(char* buffer, uint bytes) => new(buffer, 0, Math.Max(0, (int)(bytes / sizeof(char)) - 1));

    private static REG_SAM_FLAGS View(bool registry32) => registry32 ? REG_SAM_FLAGS.KEY_WOW64_32KEY : REG_SAM_FLAGS.KEY_WOW64_64KEY;
}
