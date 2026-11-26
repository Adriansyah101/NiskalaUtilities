using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Niskala.Interop.Processes;

public enum JobMessageKind
{
    Other,
    NewProcess,
    ExitProcess,
    AbnormalExitProcess,
    ActiveProcessZero,
    Wake,
}

public readonly record struct JobMessage(JobMessageKind Kind, int ProcessId);

public sealed class JobObject : IDisposable
{
    public const int ErrorSuccess = 0;

    private const nuint JobKey = 1;
    private const nuint WakeKey = 2;
    private const int ErrorMoreData = 234;

    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _port;

    private JobObject(SafeFileHandle job, SafeFileHandle port)
    {
        _job = job;
        _port = port;
    }

    public static unsafe int TryCreate(out JobObject? job)
    {
        job = null;
        SafeFileHandle handle = PInvoke.CreateJobObject(null, null);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error;
        }

        var port = new SafeFileHandle(PInvoke.CreateIoCompletionPort(new HANDLE(-1), HANDLE.Null, 0, 1), ownsHandle: true);
        if (port.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            port.Dispose();
            handle.Dispose();
            return error;
        }

        bool added = false;
        try
        {
            port.DangerousAddRef(ref added);
            var association = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
            {
                CompletionKey = (void*)JobKey,
                CompletionPort = new HANDLE(port.DangerousGetHandle()),
            };

            if (!PInvoke.SetInformationJobObject(
                    handle,
                    JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation,
                    MemoryMarshal.AsBytes(new ReadOnlySpan<JOBOBJECT_ASSOCIATE_COMPLETION_PORT>(in association))))
            {
                int error = Marshal.GetLastPInvokeError();
                port.Dispose();
                handle.Dispose();
                return error;
            }
        }
        finally
        {
            if (added)
            {
                port.DangerousRelease();
            }
        }

        job = new JobObject(handle, port);
        return ErrorSuccess;
    }

    public int TryStart(string applicationName, string commandLine, string workingDirectory, out int processId, out SafeProcessHandle? process)
    {
        int error = TryCreate(applicationName, commandLine, workingDirectory, suspended: false, out processId, out process, out SafeFileHandle? thread);
        thread?.Dispose();
        return error;
    }

    public int TryStartSuspended(string applicationName, string commandLine, string workingDirectory, out int processId, out SafeProcessHandle? process, out SafeFileHandle? thread) =>
        TryCreate(applicationName, commandLine, workingDirectory, suspended: true, out processId, out process, out thread);

    public static int TryResume(SafeFileHandle thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return PInvoke.ResumeThread(thread) == uint.MaxValue ? Marshal.GetLastPInvokeError() : ErrorSuccess;
    }

    private unsafe int TryCreate(string applicationName, string commandLine, string workingDirectory, bool suspended, out int processId, out SafeProcessHandle? process, out SafeFileHandle? thread)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationName);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        processId = 0;
        process = null;
        thread = null;

        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        void* buffer = NativeMemory.Alloc(size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(buffer);
        bool initialized = false;
        bool added = false;
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size))
            {
                return Marshal.GetLastPInvokeError();
            }

            initialized = true;
            _job.DangerousAddRef(ref added);

            HANDLE job = new(_job.DangerousGetHandle());
            if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, (nuint)sizeof(HANDLE), null, null))
            {
                return Marshal.GetLastPInvokeError();
            }

            var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);

            char[] mutableCommandLine = new char[commandLine.Length + 1];
            commandLine.CopyTo(mutableCommandLine);

            PROCESS_INFORMATION info;
            fixed (char* application = applicationName)
            fixed (char* line = mutableCommandLine)
            fixed (char* directory = workingDirectory)
            {
                PROCESS_CREATION_FLAGS flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | (suspended ? PROCESS_CREATION_FLAGS.CREATE_SUSPENDED : 0);
                if (!PInvoke.CreateProcess(application, line, null, null, false, flags, null, directory, &startup.StartupInfo, &info))
                {
                    return Marshal.GetLastPInvokeError();
                }
            }

            thread = new SafeFileHandle((nint)info.hThread.Value, ownsHandle: true);
            processId = (int)info.dwProcessId;
            process = new SafeProcessHandle((nint)info.hProcess.Value, ownsHandle: true);
            return ErrorSuccess;
        }
        finally
        {
            if (initialized)
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }

            if (added)
            {
                _job.DangerousRelease();
            }

            NativeMemory.Free(buffer);
        }
    }

    public unsafe JobMessage? Wait(TimeSpan timeout)
    {
        uint milliseconds = timeout == Timeout.InfiniteTimeSpan ? uint.MaxValue : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        if (!PInvoke.GetQueuedCompletionStatus(_port, out uint message, out nuint key, out NativeOverlapped* overlapped, milliseconds))
        {
            return null;
        }

        if (key == WakeKey)
        {
            return new JobMessage(JobMessageKind.Wake, 0);
        }

        int processId = (int)(nint)overlapped;
        JobMessageKind kind = message switch
        {
            PInvoke.JOB_OBJECT_MSG_NEW_PROCESS => JobMessageKind.NewProcess,
            PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS => JobMessageKind.ExitProcess,
            PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => JobMessageKind.AbnormalExitProcess,
            PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => JobMessageKind.ActiveProcessZero,
            _ => JobMessageKind.Other,
        };

        return new JobMessage(kind, processId);
    }

    public unsafe void Wake() => PInvoke.PostQueuedCompletionStatus(_port, 0, WakeKey, null);

    public unsafe int TryGetProcessIds(out int[] processIds)
    {
        processIds = [];
        int headerSize = sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST) - sizeof(nuint);
        for (int capacity = 64; ; capacity *= 4)
        {
            byte[] buffer = new byte[headerSize + (capacity * sizeof(nuint))];
            if (!PInvoke.QueryInformationJobObject(_job, JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, buffer, out _))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorMoreData && capacity < 65536)
                {
                    continue;
                }

                return error;
            }

            fixed (byte* p = buffer)
            {
                var list = (JOBOBJECT_BASIC_PROCESS_ID_LIST*)p;
                var ids = new ReadOnlySpan<nuint>(p + headerSize, (int)list->NumberOfProcessIdsInList);
                processIds = new int[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                {
                    processIds[i] = (int)ids[i];
                }
            }

            return ErrorSuccess;
        }
    }

    public void Dispose()
    {
        _port.Dispose();
        _job.Dispose();
    }
}
