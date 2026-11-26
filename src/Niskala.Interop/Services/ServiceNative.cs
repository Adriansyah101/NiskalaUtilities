using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace Niskala.Interop.Services;

public enum ServiceState
{
    NotFound = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

public enum ServiceStartType
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
}

public readonly record struct ServiceStartConfig(ServiceStartType StartType, bool DelayedStart);

public static partial class ServiceNative
{
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceMarkedForDelete = 1072;
    public const int ErrorTimeout = 1460;

    private const uint ManagerConnect = 0x0001;
    private const uint QueryConfig = 0x0001;
    private const uint ChangeConfig = 0x0002;
    private const uint QueryStatus = 0x0004;
    private const uint NoChange = 0xFFFF_FFFF;
    private const uint DelayedAutoStartInfo = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const uint Stop = 0x0020;
    private const uint Delete = 0x0001_0000;
    private const uint ControlStop = 1;
    private const int ErrorServiceNotActive = 1062;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static int TryQuery(string name, out ServiceState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        state = ServiceState.NotFound;
        int error = TryOpen(name, QueryStatus, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service);
        using (manager)
        using (service)
        {
            return error == ErrorServiceDoesNotExist ? 0 : error != 0 ? error : TryState(service!, out state);
        }
    }

    public static unsafe int? TryGetProcessId(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (TryOpen(name, QueryStatus, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service) != 0)
        {
            manager?.Dispose();
            service?.Dispose();
            return null;
        }

        using (manager)
        using (service)
        {
            SERVICE_STATUS_PROCESS status;
            return PInvoke.QueryServiceStatusEx(service!, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, new Span<byte>(&status, sizeof(SERVICE_STATUS_PROCESS)), out uint _)
                && status.dwProcessId != 0
                ? (int)status.dwProcessId
                : null;
        }
    }

    public static int TryStop(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        int error = TryOpen(name, QueryStatus | Stop, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service);
        using (manager)
        using (service)
        {
            if (error != 0)
            {
                return error;
            }

            if (!PInvoke.ControlService(service!, ControlStop, out SERVICE_STATUS _))
            {
                error = Marshal.GetLastPInvokeError();
                if (error != ErrorServiceNotActive)
                {
                    return error;
                }
            }

            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                error = TryState(service!, out ServiceState state);
                if (error != 0 || state == ServiceState.Stopped)
                {
                    return error;
                }

                if (Stopwatch.GetElapsedTime(start) >= timeout)
                {
                    return ErrorTimeout;
                }

                Thread.Sleep(PollInterval);
            }
        }
    }

    public static int TryDelete(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        int error = TryOpen(name, Delete, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service);
        using (manager)
        using (service)
        {
            return error != 0 ? error : PInvoke.DeleteService(service!) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public static unsafe int TryGetStartConfig(string name, out ServiceStartConfig config)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        config = default;
        int error = TryOpen(name, QueryConfig, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service);
        using (manager)
        using (service)
        {
            if (error != 0)
            {
                return error;
            }

            QueryServiceConfigW(service!, null, 0, out uint needed);
            int sizing = Marshal.GetLastPInvokeError();
            if (sizing != ErrorInsufficientBuffer)
            {
                return sizing;
            }

            byte[] buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (!QueryServiceConfigW(service!, p, needed, out _))
                {
                    return Marshal.GetLastPInvokeError();
                }

                var start = (ServiceStartType)((uint*)p)[1];
                uint delayed = 0;
                bool delayedRead = QueryServiceConfig2W(service!, DelayedAutoStartInfo, (byte*)&delayed, sizeof(uint), out _);
                config = new ServiceStartConfig(start, delayedRead && delayed != 0);
                return 0;
            }
        }
    }

    public static int TrySetStartType(string name, ServiceStartType startType)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        int error = TryOpen(name, ChangeConfig | QueryConfig, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service);
        using (manager)
        using (service)
        {
            if (error != 0)
            {
                return error;
            }

            return ChangeServiceConfigW(service!, NoChange, (uint)startType, NoChange, null, null, 0, null, null, null, null)
                ? 0
                : Marshal.GetLastPInvokeError();
        }
    }

    private static int TryOpen(string name, uint access, out CloseServiceHandleSafeHandle? manager, out CloseServiceHandleSafeHandle? service)
    {
        service = null;
        manager = PInvoke.OpenSCManager(null, null, ManagerConnect);
        if (manager.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        service = PInvoke.OpenService(manager, name, access);
        return service.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    private static unsafe int TryState(CloseServiceHandleSafeHandle service, out ServiceState state)
    {
        state = ServiceState.NotFound;
        SERVICE_STATUS_PROCESS status;
        if (!PInvoke.QueryServiceStatusEx(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, new Span<byte>(&status, sizeof(SERVICE_STATUS_PROCESS)), out uint _))
        {
            return Marshal.GetLastPInvokeError();
        }

        state = (ServiceState)status.dwCurrentState;
        return 0;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceConfigW(SafeHandle service, byte* config, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceConfig2W(SafeHandle service, uint level, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(
        SafeHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);
}
