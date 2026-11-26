using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Niskala.Interop.Security;

public enum SystemPrivilege
{
    Debug,
    Backup,
    Restore,
}

public sealed class PrivilegeScope : IDisposable
{
    private const int ErrorNotAllAssigned = 1300;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<SystemPrivilege, int> Holders = [];
    private static readonly HashSet<SystemPrivilege> TurnedOn = [];

    private readonly SystemPrivilege[] _enabled;
    private bool _disposed;

    private PrivilegeScope(SystemPrivilege[] enabled, SystemPrivilege[] missing)
    {
        _enabled = enabled;
        Missing = missing;
    }

    public IReadOnlyList<SystemPrivilege> Enabled => _enabled;

    public IReadOnlyList<SystemPrivilege> Missing { get; }

    public static PrivilegeScope Enable(params ReadOnlySpan<SystemPrivilege> privileges)
    {
        var enabled = new List<SystemPrivilege>(privileges.Length);
        var missing = new List<SystemPrivilege>();
        lock (Gate)
        {
            foreach (SystemPrivilege privilege in privileges)
            {
                if (enabled.Contains(privilege) || missing.Contains(privilege))
                {
                    continue;
                }

                int holders = Holders.GetValueOrDefault(privilege);
                if (holders > 0)
                {
                    Holders[privilege] = holders + 1;
                    enabled.Add(privilege);
                }
                else if (TryAdjust(privilege, enable: true, out bool wasEnabled) == 0)
                {
                    Holders[privilege] = 1;
                    if (!wasEnabled)
                    {
                        TurnedOn.Add(privilege);
                    }

                    enabled.Add(privilege);
                }
                else
                {
                    missing.Add(privilege);
                }
            }
        }

        return new PrivilegeScope([.. enabled], [.. missing]);
    }

    public static unsafe bool IsEnabled(SystemPrivilege privilege)
    {
        if (!PInvoke.LookupPrivilegeValue(null, Name(privilege), out LUID luid) || OpenToken(TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token) != 0)
        {
            return false;
        }

        using (token)
        {
            uint size = 0;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenPrivileges, null, 0, out size);
            byte[] buffer = new byte[size];
            fixed (byte* bytes = buffer)
            {
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenPrivileges, bytes, size, out size))
                {
                    return false;
                }

                var list = (TOKEN_PRIVILEGES*)bytes;
                foreach (LUID_AND_ATTRIBUTES entry in list->Privileges.AsSpan((int)list->PrivilegeCount))
                {
                    if (entry.Luid.LowPart == luid.LowPart && entry.Luid.HighPart == luid.HighPart)
                    {
                        return (entry.Attributes & TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED) != 0;
                    }
                }

                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (SystemPrivilege privilege in _enabled)
            {
                int remaining = Holders[privilege] - 1;
                Holders[privilege] = remaining;
                if (remaining == 0 && TurnedOn.Remove(privilege))
                {
                    TryAdjust(privilege, enable: false, out _);
                }
            }
        }
    }

    private static unsafe int TryAdjust(SystemPrivilege privilege, bool enable, out bool wasEnabled)
    {
        wasEnabled = false;
        if (!PInvoke.LookupPrivilegeValue(null, Name(privilege), out LUID luid))
        {
            return Marshal.GetLastPInvokeError();
        }

        int error = OpenToken(TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token);
        if (error != 0)
        {
            return error;
        }

        using (token)
        {
            var requested = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            requested.Privileges[0] = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = enable ? TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED : 0,
            };

            TOKEN_PRIVILEGES previous = default;
            uint length = 0;
            if (!PInvoke.AdjustTokenPrivileges((HANDLE)token.DangerousGetHandle(), false, &requested, (uint)sizeof(TOKEN_PRIVILEGES), &previous, &length))
            {
                return Marshal.GetLastPInvokeError();
            }

            error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotAllAssigned)
            {
                return error;
            }

            wasEnabled = previous.PrivilegeCount == 0 || (previous.Privileges[0].Attributes & TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED) != 0;
            return 0;
        }
    }

    private static int OpenToken(TOKEN_ACCESS_MASK access, out SafeFileHandle token)
    {
        using var process = new SafeFileHandle(PInvoke.GetCurrentProcess(), ownsHandle: false);
        if (PInvoke.OpenProcessToken(process, access, out token))
        {
            return 0;
        }

        int error = Marshal.GetLastPInvokeError();
        token.Dispose();
        return error;
    }

    private static string Name(SystemPrivilege privilege) => privilege switch
    {
        SystemPrivilege.Debug => "SeDebugPrivilege",
        SystemPrivilege.Backup => "SeBackupPrivilege",
        SystemPrivilege.Restore => "SeRestorePrivilege",
        _ => throw new ArgumentOutOfRangeException(nameof(privilege)),
    };
}
