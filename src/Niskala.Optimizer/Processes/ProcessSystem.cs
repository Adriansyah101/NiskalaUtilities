using Niskala.Interop.Desktop;
using Niskala.Interop.Processes;

namespace Niskala.Optimizer.Processes;

public interface IProcessSystem
{
    int CurrentProcessId { get; }

    string WindowsDirectory { get; }

    IReadOnlyList<string> OwnDirectories { get; }

    IReadOnlyList<ProcessEntry> Snapshot();

    IReadOnlySet<int> ProcessesWithVisibleWindows();

    string? ImagePath(int processId);

    int? SessionId(int processId);

    bool? IsCritical(int processId);

    ProcessTimes? Times(int processId);

    PriorityClass? Priority(int processId);

    int Suspend(int processId);

    int Resume(int processId);

    int SetEfficiency(int processId, bool efficient);

    int SetPriority(int processId, PriorityClass priority);
}

public sealed class SystemProcesses : IProcessSystem
{
    public static SystemProcesses Instance { get; } = new();

    public int CurrentProcessId => Environment.ProcessId;

    public string WindowsDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<string> OwnDirectories { get; } = Own();

    public IReadOnlyList<ProcessEntry> Snapshot() =>
        ProcessNative.TrySnapshot(out List<ProcessEntry> processes) == ProcessNative.ErrorSuccess ? processes : [];

    public IReadOnlySet<int> ProcessesWithVisibleWindows() =>
        WindowNative.ZOrder().Where(w => w.Visible && !w.Cloaked).Select(w => w.ProcessId).ToHashSet();

    public string? ImagePath(int processId) => ProcessNative.TryGetImagePath(processId);

    public int? SessionId(int processId) => ProcessRestraintNative.TryGetSessionId(processId, out int session) == 0 ? session : null;

    public bool? IsCritical(int processId) => ProcessNative.IsCritical(processId);

    public ProcessTimes? Times(int processId) => ProcessRestraintNative.TryGetTimes(processId, out ProcessTimes times) == 0 ? times : null;

    public PriorityClass? Priority(int processId) => ProcessRestraintNative.TryGetPriority(processId, out PriorityClass priority) == 0 ? priority : null;

    public int Suspend(int processId) => ProcessRestraintNative.TrySuspend(processId);

    public int Resume(int processId) => ProcessRestraintNative.TryResume(processId);

    public int SetEfficiency(int processId, bool efficient) => ProcessRestraintNative.TrySetEfficiencyMode(processId, efficient);

    public int SetPriority(int processId, PriorityClass priority) => ProcessRestraintNative.TrySetPriority(processId, priority);

    private static string[] Own()
    {
        string own = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Path.GetFileName(own).Equals("lib", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(own) is { } parent ? [own, parent] : [own];
    }
}
