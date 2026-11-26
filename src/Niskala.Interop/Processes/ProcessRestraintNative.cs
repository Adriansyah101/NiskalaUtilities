using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Niskala.Interop.Security;

namespace Niskala.Interop.Processes;

public enum PriorityClass
{
    Idle = 0x0040,
    BelowNormal = 0x4000,
    Normal = 0x0020,
    AboveNormal = 0x8000,
    High = 0x0080,
    Realtime = 0x0100,
}

public readonly record struct ProcessTimes(DateTime StartedUtc, TimeSpan Cpu);

public static partial class ProcessRestraintNative
{
    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;

    private const uint QueryLimitedInformation = 0x1000;
    private const uint SetInformation = 0x0200;
    private const uint SuspendResume = 0x0800;
    private const uint SetQuota = 0x0100;
    private const uint Terminate = 0x0001;
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;
    private const int CpuRateScale = 100;

    public static int TryGetSessionId(int processId, out int sessionId)
    {
        bool found = ProcessIdToSessionId((uint)processId, out uint session);
        sessionId = (int)session;
        return found ? ErrorSuccess : Marshal.GetLastPInvokeError();
    }

    public static int TryGetTimes(int processId, out ProcessTimes times)
    {
        times = default;
        using SafeProcessHandle process = Open(processId, QueryLimitedInformation);
        if (process.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        if (!GetProcessTimes(process, out long created, out _, out long kernel, out long user))
        {
            return Marshal.GetLastPInvokeError();
        }

        times = new ProcessTimes(DateTime.FromFileTimeUtc(created), TimeSpan.FromTicks(kernel + user));
        return ErrorSuccess;
    }

    public static int TryGetPriority(int processId, out PriorityClass priority)
    {
        priority = PriorityClass.Normal;
        using SafeProcessHandle process = Open(processId, QueryLimitedInformation);
        if (process.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        uint value = GetPriorityClass(process);
        if (value == 0)
        {
            return Marshal.GetLastPInvokeError();
        }

        priority = (PriorityClass)value;
        return ErrorSuccess;
    }

    public static int TrySetPriority(int processId, PriorityClass priority)
    {
        using SafeProcessHandle process = Open(processId, SetInformation | QueryLimitedInformation);
        return process.IsInvalid ? Marshal.GetLastPInvokeError()
            : SetPriorityClass(process, (uint)priority) ? ErrorSuccess : Marshal.GetLastPInvokeError();
    }

    public static unsafe int TrySetEfficiencyMode(int processId, bool efficient)
    {
        using SafeProcessHandle process = Open(processId, SetInformation | QueryLimitedInformation);
        if (process.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingVersion,
            ControlMask = efficient ? ExecutionSpeed : 0,
            StateMask = efficient ? ExecutionSpeed : 0,
        };
        return SetProcessInformation(process, ProcessPowerThrottling, &state, (uint)sizeof(PowerThrottlingState))
            ? ErrorSuccess
            : Marshal.GetLastPInvokeError();
    }

    public static int TrySuspend(int processId) => Toggle(processId, suspend: true);

    public static int TryResume(int processId) => Toggle(processId, suspend: false);

    internal static SafeProcessHandle OpenForJob(int processId) => Open(processId, SetQuota | Terminate | QueryLimitedInformation);

    internal static unsafe int TrySetCpuRate(SafeHandle job, int? percent)
    {
        var info = new CpuRateControl
        {
            ControlFlags = percent is null ? 0 : CpuRateEnable | CpuRateHardCap,
            CpuRate = percent is { } value ? (uint)(Math.Clamp(value, 1, 100) * CpuRateScale) : 0,
        };
        return SetInformationJobObject(job, JobObjectCpuRateControlInformation, &info, (uint)sizeof(CpuRateControl))
            ? ErrorSuccess
            : Marshal.GetLastPInvokeError();
    }

    internal static SafeFileHandle CreateJob() => CreateJobObject(0, null);

    internal static int TryAssign(SafeHandle job, SafeProcessHandle process) =>
        AssignProcessToJobObject(job, process) ? ErrorSuccess : Marshal.GetLastPInvokeError();

    private static int Toggle(int processId, bool suspend)
    {
        SafeProcessHandle process;
        int error;
        using (PrivilegeScope.Enable(SystemPrivilege.Debug))
        {
            process = Open(processId, SuspendResume | QueryLimitedInformation);
            error = process.IsInvalid ? Marshal.GetLastPInvokeError() : ErrorSuccess;
        }

        using (process)
        {
            if (error != ErrorSuccess)
            {
                return error;
            }

            int status = suspend ? NtSuspendProcess(process) : NtResumeProcess(process);
            return status >= 0 ? ErrorSuccess : (int)RtlNtStatusToDosError(status);
        }
    }

    private static SafeProcessHandle Open(int processId, uint access) => new(OpenProcess(access, false, (uint)processId), ownsHandle: true);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(SafeProcessHandle process, uint priority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetProcessInformation(SafeProcessHandle process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeHandle job, int informationClass, void* information, uint size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
}

public sealed class CpuCapJob : IDisposable
{
    private readonly SafeFileHandle _job;

    private CpuCapJob(SafeFileHandle job, int processId, int percent)
    {
        _job = job;
        ProcessId = processId;
        Percent = percent;
    }

    public int ProcessId { get; }

    public int Percent { get; private set; }

    public static int TryCreate(int processId, int percent, out CpuCapJob? cap)
    {
        cap = null;
        SafeFileHandle job = ProcessRestraintNative.CreateJob();
        if (job.IsInvalid)
        {
            int created = Marshal.GetLastPInvokeError();
            job.Dispose();
            return created;
        }

        int error = ProcessRestraintNative.TrySetCpuRate(job, percent);
        if (error == 0)
        {
            using SafeProcessHandle process = ProcessRestraintNative.OpenForJob(processId);
            error = process.IsInvalid ? Marshal.GetLastPInvokeError() : ProcessRestraintNative.TryAssign(job, process);
        }

        if (error != 0)
        {
            job.Dispose();
            return error;
        }

        cap = new CpuCapJob(job, processId, percent);
        return 0;
    }

    public int Change(int percent)
    {
        int error = ProcessRestraintNative.TrySetCpuRate(_job, percent);
        if (error == 0)
        {
            Percent = percent;
        }

        return error;
    }

    public int Lift() => ProcessRestraintNative.TrySetCpuRate(_job, null);

    public void Dispose()
    {
        if (!_job.IsClosed)
        {
            Lift();
            _job.Dispose();
        }
    }
}
