using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Niskala.Interop.Security;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace Niskala.Interop.Processes;

public readonly record struct ProcessEntry(int Id, string ExeName, int ParentId = 0);

public enum TerminateOutcome
{
    Terminated,
    Exited,
    ImageChanged,
    CriticalProcess,
    StillRunning,
    Failed,
}

public readonly record struct TerminateResult(TerminateOutcome Outcome, int Error);

public static class ProcessNative
{
    public const int ErrorSuccess = 0;

    private const uint StillActiveExitCode = 259;
    private const uint TerminatedExitCode = 1;
    private const int ErrorInvalidParameter = 87;
    private const int PackageNameCapacity = 128;

    public static unsafe int TrySnapshot(out List<ProcessEntry> processes)
    {
        processes = [];
        HANDLE snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (NativeHandle.IsInvalid(snapshot))
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            for (bool more = PInvoke.Process32FirstW(snapshot, &entry); more; more = PInvoke.Process32NextW(snapshot, &entry))
            {
                processes.Add(new ProcessEntry((int)entry.th32ProcessID, entry.szExeFile.ToString(), (int)entry.th32ParentProcessID));
            }

            return ErrorSuccess;
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }
    }

    public static string? TryGetImagePath(int processId)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            return ImagePath(process);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    public static unsafe string? TryGetPackageFullName(int processId)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            char* buffer = stackalloc char[PackageNameCapacity];
            uint length = PackageNameCapacity;
            return PInvoke.GetPackageFullName(process, &length, buffer) == WIN32_ERROR.ERROR_SUCCESS
                ? new string(buffer, 0, Math.Max(0, (int)length - 1))
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    public static TerminateResult Terminate(int processId, string expectedImage, TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedImage);
        const PROCESS_ACCESS_RIGHTS Access =
            PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE;
        HANDLE process;
        int error;
        using (PrivilegeScope.Enable(SystemPrivilege.Debug))
        {
            process = PInvoke.OpenProcess(Access, false, (uint)processId);
            error = process.IsNull ? Marshal.GetLastPInvokeError() : ErrorSuccess;
        }

        if (process.IsNull)
        {
            return error == ErrorInvalidParameter ? new TerminateResult(TerminateOutcome.Exited, 0) : new TerminateResult(TerminateOutcome.Failed, error);
        }

        try
        {
            if (!string.Equals(ImagePath(process), expectedImage, StringComparison.OrdinalIgnoreCase))
            {
                return new TerminateResult(TerminateOutcome.ImageChanged, 0);
            }

            if (IsCritical(process))
            {
                return new TerminateResult(TerminateOutcome.CriticalProcess, 0);
            }

            if (!PInvoke.TerminateProcess(process, TerminatedExitCode))
            {
                return new TerminateResult(TerminateOutcome.Failed, Marshal.GetLastPInvokeError());
            }

            return PInvoke.WaitForSingleObject(process, (uint)wait.TotalMilliseconds) == WAIT_EVENT.WAIT_OBJECT_0
                ? new TerminateResult(TerminateOutcome.Terminated, 0)
                : new TerminateResult(TerminateOutcome.StillRunning, 0);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    public static bool? IsCritical(int processId)
    {
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            return IsCritical(process);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static unsafe bool IsCritical(HANDLE process)
    {
        BOOL critical;
        return !PInvoke.IsProcessCritical(process, &critical) || critical;
    }

    private static unsafe string? ImagePath(HANDLE process)
    {
        const int Capacity = 32768;
        char[] buffer = new char[Capacity];
        uint length = Capacity;
        fixed (char* p = buffer)
        {
            return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, p, &length)
                ? new string(buffer, 0, (int)length)
                : null;
        }
    }

    public static int? TryGetExitCode(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return PInvoke.GetExitCodeProcess(process, out uint code) && code != StillActiveExitCode ? (int)code : null;
    }
}
