using Niskala.Interop.Etw;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Tracing;

namespace Niskala.Tracker.Recording;

public sealed record RecorderOptions(int? ServicesProcessId, Func<int?> InstallerServiceProcessId, Func<int, string?> ImagePath);

public sealed record RecordedTrace(IReadOnlyList<EtwEvent> Events, IReadOnlyList<RecordedProcess> Processes, int UnmappedPaths);

public sealed class InstallRecorder(KernelPathTranslator paths, RecorderOptions options, TimeProvider? time = null)
{
    private const string WindowsInstaller = "msiexec.exe";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly List<EtwEvent> _events = [];
    private readonly List<RecordedProcess> _processes = [];
    private readonly Dictionary<int, int> _running = [];
    private Action<int>? _track;
    private bool _installerTraced;
    private int _unmapped;

    public event Action<RecordedProcess>? ProcessChanged;

    public void AddRoot(int processId, string imagePath, Action<int> track)
    {
        ArgumentNullException.ThrowIfNull(track);
        RecordedProcess root = new(processId, 0, imagePath, _time.GetUtcNow(), Adopted: false);
        lock (_lock)
        {
            _track = track;
            Add(root);
        }

        track(processId);
        ProcessChanged?.Invoke(root);
        if (IsWindowsInstaller(imagePath))
        {
            AdoptInstallerService();
        }
    }

    public bool OnEvent(EtwEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.Kind)
        {
            case EtwEventKind.ProcessStart:
                return OnStart(e);
            case EtwEventKind.ProcessStop:
                OnStop(e);
                return false;
            default:
                Record(e);
                return false;
        }
    }

    public RecordedTrace Snapshot()
    {
        lock (_lock)
        {
            return new RecordedTrace([.. _events], [.. _processes], _unmapped);
        }
    }

    public IReadOnlyList<RecordedProcess> Running()
    {
        lock (_lock)
        {
            return [.. _running.Values.Select(i => _processes[i]).Where(p => !p.Adopted)];
        }
    }

    private bool OnStart(EtwEvent e)
    {
        string image = paths.File(e.Path) ?? e.Path;
        RecordedProcess? added = null;
        bool firstInstaller = false;
        lock (_lock)
        {
            if (_running.TryGetValue(e.ProcessId, out int index) && _processes[index].ParentId == 0)
            {
                _processes[index] = _processes[index] with { ParentId = e.ParentProcessId };
                return true;
            }

            bool parentTraced = e.Flags.HasFlag(EtwEventFlags.ParentTracked);
            bool adopt = !parentTraced && _installerTraced && e.ParentProcessId == options.ServicesProcessId && IsWindowsInstaller(image);
            if (!parentTraced && !adopt)
            {
                return false;
            }

            added = new RecordedProcess(e.ProcessId, e.ParentProcessId, image, new DateTimeOffset(e.TimeUtc), adopt);
            Add(added);
            firstInstaller = !_installerTraced && IsWindowsInstaller(image);
        }

        ProcessChanged?.Invoke(added);
        if (firstInstaller)
        {
            AdoptInstallerService();
        }

        return true;
    }

    private void OnStop(EtwEvent e)
    {
        RecordedProcess? exited = null;
        lock (_lock)
        {
            if (_running.Remove(e.ProcessId, out int index))
            {
                exited = _processes[index] = _processes[index] with { ExitedAt = new DateTimeOffset(e.TimeUtc) };
            }
        }

        if (exited is not null)
        {
            ProcessChanged?.Invoke(exited);
        }
    }

    private void AdoptInstallerService()
    {
        RecordedProcess? adopted = null;
        Action<int>? track;
        lock (_lock)
        {
            _installerTraced = true;
            track = _track;
            if (options.InstallerServiceProcessId() is { } service && !_running.ContainsKey(service))
            {
                adopted = new RecordedProcess(service, options.ServicesProcessId ?? 0, options.ImagePath(service), _time.GetUtcNow(), Adopted: true);
                Add(adopted);
            }
        }

        if (adopted is not null && track is not null)
        {
            track(adopted.Id);
            ProcessChanged?.Invoke(adopted);
        }
    }

    private void Record(EtwEvent e)
    {
        bool isFile = e.Kind is EtwEventKind.FileCreated or EtwEventKind.FileWritten or EtwEventKind.FileDeleted or EtwEventKind.FileRenamed;
        EtwEvent? translated = e.Kind switch
        {
            EtwEventKind.FileRenamed => paths.File(e.Name ?? string.Empty) is { } target
                ? e with { Path = e.Path.Length == 0 ? string.Empty : paths.File(e.Path) ?? string.Empty, Name = target }
                : null,
            _ when isFile => paths.File(e.Path) is { } file ? e with { Path = file } : null,
            _ => paths.Key(e.Path) is { } key ? e with { Path = key } : null,
        };

        lock (_lock)
        {
            if (translated is null)
            {
                _unmapped++;
            }
            else
            {
                _events.Add(translated);
            }
        }
    }

    private void Add(RecordedProcess process)
    {
        _running[process.Id] = _processes.Count;
        _processes.Add(process);
    }

    private static bool IsWindowsInstaller(string? image) =>
        string.Equals(Path.GetFileName(image), WindowsInstaller, StringComparison.OrdinalIgnoreCase);
}
