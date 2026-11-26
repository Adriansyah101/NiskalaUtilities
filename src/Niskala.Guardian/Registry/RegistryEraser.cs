using System.Globalization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Registry;

namespace Niskala.Guardian.Registry;

public readonly record struct RestoreCounts(int Written, int Kept);

public static class RegistryEraser
{
    public static int TryOpen(RegistryPath path, bool writable, out SafeRegistryHandle key)
    {
        ArgumentNullException.ThrowIfNull(path);
        return RegistryNative.TryOpenNoFollow(RegistryNative.Hive(path.Hive), path.PhysicalSubKey, Is32(path), writable, out key);
    }

    public static RegistryKeyBackup? TryCapture(SafeRegistryHandle key, RegistryPath path, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(path);
        if (RegistryNative.IsLink(key))
        {
            problem = "the key is a registry link";
            return null;
        }

        return Capture(key, path.Segments[^1], Is32(path), path.LogicalPath, out problem);
    }

    public static int TryDeleteTree(SafeRegistryHandle key, RegistryPath path, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(path);
        if (RegistryNative.IsLink(key))
        {
            problem = "the key is a registry link";
            return RegistryNative.ErrorAccessDenied;
        }

        return DeleteTree(key, Is32(path), path.LogicalPath, out problem);
    }

    public static int TryRestore(RegistryPath path, RegistryKeyBackup backup, out RestoreCounts counts)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(backup);
        int written = 0;
        int kept = 0;
        int error = Restore(RegistryNative.Hive(path.Hive), path.PhysicalSubKey, backup, Is32(path), ref written, ref kept);
        counts = new RestoreCounts(written, kept);
        return error;
    }

    public static int TryRestoreValue(RegistryPath path, RegistryValueBackup value, out bool written)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        written = false;
        int error = RegistryNative.TryCreateKey(RegistryNative.Hive(path.Hive), path.PhysicalSubKey, Is32(path), out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error;
            }

            error = RegistryNative.TryReadValue(key, value.Name, out _);
            if (error != RegistryNative.ErrorFileNotFound)
            {
                return error;
            }

            written = true;
            return RegistryNative.TrySetValue(key, new RawRegistryValue(value.Name, value.Kind, value.Data));
        }
    }

    private static RegistryKeyBackup? Capture(SafeRegistryHandle key, string name, bool registry32, string display, out string? problem)
    {
        problem = null;
        int error = RegistryNative.TryReadValues(key, out List<RawRegistryValue> values);
        if (error == 0)
        {
            error = RegistryNative.TryListSubKeys(key, out List<string> names);
            if (error == 0)
            {
                var subKeys = new List<RegistryKeyBackup>(names.Count);
                foreach (string child in names)
                {
                    error = RegistryNative.TryOpenNoFollow(key, child, registry32, writable: false, out SafeRegistryHandle childKey);
                    using (childKey)
                    {
                        if (error != 0)
                        {
                            problem = Failure(error, $@"{display}\{child}");
                            return null;
                        }

                        if (RegistryNative.IsLink(childKey))
                        {
                            problem = $@"{display}\{child} is a registry link";
                            return null;
                        }

                        if (Capture(childKey, child, registry32, $@"{display}\{child}", out problem) is not { } captured)
                        {
                            return null;
                        }

                        subKeys.Add(captured);
                    }
                }

                return new RegistryKeyBackup(name, [.. values.Select(v => new RegistryValueBackup(v.Name, v.Kind, v.Data))], subKeys);
            }
        }

        problem = Failure(error, display);
        return null;
    }

    private static int DeleteTree(SafeRegistryHandle key, bool registry32, string display, out string? problem)
    {
        problem = null;
        int error = RegistryNative.TryListSubKeys(key, out List<string> names);
        if (error != 0)
        {
            problem = Failure(error, display);
            return error;
        }

        foreach (string child in names)
        {
            error = RegistryNative.TryOpenNoFollow(key, child, registry32, writable: true, out SafeRegistryHandle childKey);
            using (childKey)
            {
                if (error != 0)
                {
                    problem = Failure(error, $@"{display}\{child}");
                    return error;
                }

                if (RegistryNative.IsLink(childKey))
                {
                    problem = $@"{display}\{child} is a registry link";
                    return RegistryNative.ErrorAccessDenied;
                }

                error = DeleteTree(childKey, registry32, $@"{display}\{child}", out problem);
                if (error != 0)
                {
                    return error;
                }
            }
        }

        error = RegistryNative.TryDeleteKey(key);
        if (error != 0)
        {
            problem = Failure(error, display);
        }

        return error;
    }

    private static int Restore(SafeRegistryHandle parent, string subKey, RegistryKeyBackup backup, bool registry32, ref int written, ref int kept)
    {
        int error = RegistryNative.TryCreateKey(parent, subKey, registry32, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error;
            }

            foreach (RegistryValueBackup value in backup.Values)
            {
                error = RegistryNative.TryReadValue(key, value.Name, out _);
                if (error == 0)
                {
                    kept++;
                    continue;
                }

                if (error != RegistryNative.ErrorFileNotFound)
                {
                    return error;
                }

                error = RegistryNative.TrySetValue(key, new RawRegistryValue(value.Name, value.Kind, value.Data));
                if (error != 0)
                {
                    return error;
                }

                written++;
            }

            foreach (RegistryKeyBackup child in backup.SubKeys)
            {
                error = Restore(key, child.Name, child, registry32, ref written, ref kept);
                if (error != 0)
                {
                    return error;
                }
            }

            return 0;
        }
    }

    private static string Failure(int error, string display) => error == RegistryNative.ErrorAccessDenied
        ? $"access denied to {display}"
        : string.Create(CultureInfo.InvariantCulture, $"{display}: Win32 error {error}");

    private static bool Is32(RegistryPath path) => path.View == RegistryView.Registry32;
}
