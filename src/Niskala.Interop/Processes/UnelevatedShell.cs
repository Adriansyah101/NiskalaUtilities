using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Niskala.Interop.Processes;

public static class UnelevatedShell
{
    private const int ErrorSuccess = 0;
    private const int ErrorInvalidWindowHandle = 1400;

    public static unsafe int TryStart(string applicationName, string commandLine)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationName);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);

        HWND shell = PInvoke.GetShellWindow();
        uint shellProcessId = 0;
        if (shell.IsNull || PInvoke.GetWindowThreadProcessId(shell, &shellProcessId) == 0 || shellProcessId == 0)
        {
            return ErrorInvalidWindowHandle;
        }

        HANDLE parent = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_CREATE_PROCESS, false, shellProcessId);
        if (parent.IsNull)
        {
            return Marshal.GetLastPInvokeError();
        }

        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        void* buffer = NativeMemory.Alloc(size);
        var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(buffer);
        bool initialized = false;
        try
        {
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size))
            {
                return Marshal.GetLastPInvokeError();
            }

            initialized = true;

            if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, &parent, (nuint)sizeof(HANDLE), null, null))
            {
                return Marshal.GetLastPInvokeError();
            }

            var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            char[] line = new char[commandLine.Length + 1];
            commandLine.CopyTo(line);

            PROCESS_INFORMATION info;
            fixed (char* application = applicationName)
            fixed (char* mutableLine = line)
            {
                if (!PInvoke.CreateProcess(application, mutableLine, null, null, false, PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT, null, null, &startup.StartupInfo, &info))
                {
                    return Marshal.GetLastPInvokeError();
                }
            }

            PInvoke.CloseHandle(info.hThread);
            PInvoke.CloseHandle(info.hProcess);
            return ErrorSuccess;
        }
        finally
        {
            if (initialized)
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }

            NativeMemory.Free(buffer);
            PInvoke.CloseHandle(parent);
        }
    }
}
