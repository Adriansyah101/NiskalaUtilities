using Niskala.Interop.Processes;

namespace Niskala.Uninstall.Preflight;

public sealed record RunningProcess(int Id, string Name, string? ImagePath);

public interface IPreflightEnvironment
{
    bool IsElevated { get; }

    bool IsInstallerBusy();

    IReadOnlyList<RunningProcess> RunningProcesses();
}

public sealed class SystemPreflightEnvironment : IPreflightEnvironment
{
    public static SystemPreflightEnvironment Instance { get; } = new();

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public bool IsInstallerBusy() => WindowsInstallerMutex.IsHeld();

    public IReadOnlyList<RunningProcess> RunningProcesses()
    {
        if (ProcessNative.TrySnapshot(out List<ProcessEntry> processes) != ProcessNative.ErrorSuccess)
        {
            return [];
        }

        return [.. processes
            .Where(p => p.Id > 4)
            .Select(p => new RunningProcess(p.Id, p.ExeName, ProcessNative.TryGetImagePath(p.Id)))];
    }
}

public static class WindowsInstallerMutex
{
    private const string Name = @"Global\_MSIExecute";

    public static bool IsHeld()
    {
        try
        {
            if (Mutex.TryOpenExisting(Name, out Mutex? mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
