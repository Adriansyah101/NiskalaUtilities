using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Registry;
using Niskala.Interop.Registry;

namespace Niskala.Tracker.Recording;

public enum FileState
{
    Missing,
    File,
    Directory,
}

public interface ISystemState
{
    FileState File(string path);

    bool KeyExists(string key);

    RawRegistryValue? Value(string key, string name);
}

public sealed class LiveSystemState : ISystemState
{
    public static LiveSystemState Instance { get; } = new();

    public FileState File(string path)
    {
        try
        {
            return (System.IO.File.GetAttributes(path) & FileAttributes.Directory) != 0 ? FileState.Directory : FileState.File;
        }
        catch (UnauthorizedAccessException)
        {
            return FileState.File;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return FileState.Missing;
        }
    }

    public bool KeyExists(string key)
    {
        using SafeRegistryHandle? handle = Open(key);
        return handle is not null;
    }

    public RawRegistryValue? Value(string key, string name)
    {
        using SafeRegistryHandle? handle = Open(key);
        return handle is not null && RegistryNative.TryReadValue(handle, name, out RawRegistryValue? value) == 0 ? value : null;
    }

    private static SafeRegistryHandle? Open(string key)
    {
        if (RegistryPath.TryParse(key, RegistryView.Default, out RegistryPath? path) != RegistryVerdict.Allowed)
        {
            return null;
        }

        int error = RegistryEraser.TryOpen(path!, writable: false, out SafeRegistryHandle handle);
        if (error == 0)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }
}
