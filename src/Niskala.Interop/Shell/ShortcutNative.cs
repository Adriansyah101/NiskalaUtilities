using System.Runtime.InteropServices;
using Niskala.Interop.Com;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Niskala.Interop.Shell;

public sealed record ShortcutTarget(string LinkPath, string? Path, string? Arguments, string? MsiProductCode);

public static class ShortcutNative
{
    private const int GuidBufferLength = 39;
    private const int PathCapacity = 1024;
    private const int ArgumentsCapacity = 1024;

    public static List<ShortcutTarget> Read(IEnumerable<string> linkPaths)
    {
        ArgumentNullException.ThrowIfNull(linkPaths);
        using ComScope com = ComScope.Enter();
        var targets = new List<ShortcutTarget>();
        foreach (string link in linkPaths)
        {
            if (TryRead(link) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    private static unsafe ShortcutTarget? TryRead(string link)
    {
        char* product = stackalloc char[GuidBufferLength];
        fixed (char* p = link)
        {
            if (PInvoke.MsiGetShortcutTarget(p, product, default, default) == 0)
            {
                return new ShortcutTarget(link, null, null, new string(product));
            }
        }

        IShellLinkW* shellLink = null;
        IPersistFile* file = null;
        try
        {
            if (ShellLink.CreateInstance(out shellLink).Failed || shellLink->QueryInterface(out file).Failed)
            {
                return null;
            }

            fixed (char* p = link)
            {
                file->Load(p, STGM.STGM_READ);
            }

            char* path = stackalloc char[PathCapacity];
            char* arguments = stackalloc char[ArgumentsCapacity];
            *path = '\0';
            *arguments = '\0';
            HRESULT result = shellLink->GetPath(path, PathCapacity, null, 0);
            shellLink->GetArguments(arguments, ArgumentsCapacity);

            string? target = result == HRESULT.S_OK && *path != '\0' ? new string(path) : null;
            string? args = *arguments != '\0' ? new string(arguments) : null;
            return new ShortcutTarget(link, target, args, null);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (file is not null)
            {
                file->Release();
            }

            if (shellLink is not null)
            {
                shellLink->Release();
            }
        }
    }
}
