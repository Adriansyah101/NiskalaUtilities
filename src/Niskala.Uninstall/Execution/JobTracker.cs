using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Niskala.Interop.Processes;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Execution;

public enum JobEventKind
{
    Started,
    Exited,
    ExitedAbnormally,
    Empty,
}

public sealed record JobEvent(JobEventKind Kind, int ProcessId, string? ImagePath, DateTimeOffset Time);

public sealed class JobTracker : IDisposable
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromMilliseconds(500);

    private readonly JobObject _job;
    private readonly SafeProcessHandle _root;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<JobEvent> _events = Channel.CreateUnbounded<JobEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly ConcurrentDictionary<int, string?> _images = new();
    private readonly Thread _pump;
    private volatile bool _stopping;

    private JobTracker(JobObject job, SafeProcessHandle root, int rootProcessId, ILogger logger, TimeProvider time)
    {
        _job = job;
        _root = root;
        _logger = logger;
        _time = time;
        RootProcessId = rootProcessId;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Niskala job pump" };
        _pump.Start();
    }

    public int RootProcessId { get; }

    public ChannelReader<JobEvent> Events => _events.Reader;

    public int? RootExitCode => ProcessNative.TryGetExitCode(_root);

    public static int TryStart(ResolvedCommand command, ILogger<JobTracker> logger, out JobTracker? tracker, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        tracker = null;
        int error = JobObject.TryCreate(out JobObject? job);
        if (error != JobObject.ErrorSuccess)
        {
            UninstallLog.ProcessStartFailed(logger, command.CommandLine, error);
            return error;
        }

        error = job!.TryStart(command.ApplicationName, command.CommandLine, Environment.SystemDirectory, out int processId, out SafeProcessHandle? process);
        if (error != JobObject.ErrorSuccess)
        {
            job.Dispose();
            UninstallLog.ProcessStartFailed(logger, command.CommandLine, error);
            return error;
        }

        tracker = new JobTracker(job, process!, processId, logger, time ?? TimeProvider.System);
        UninstallLog.ProcessStarted(logger, processId, command.CommandLine);
        return JobObject.ErrorSuccess;
    }

    public IReadOnlyList<RunningProcess> ActiveProcesses()
    {
        int error = _job.TryGetProcessIds(out int[] ids);
        if (error != JobObject.ErrorSuccess)
        {
            UninstallLog.JobQueryFailed(_logger, error);
            return [new RunningProcess(0, "unknown", null)];
        }

        return [.. ids.Select(id =>
        {
            string? image = _images.GetOrAdd(id, ProcessNative.TryGetImagePath);
            return new RunningProcess(id, image is null ? $"pid {id}" : Path.GetFileName(image), image);
        })];
    }

    public void Dispose()
    {
        _stopping = true;
        _job.Wake();
        _pump.Join();
        _job.Dispose();
        _root.Dispose();
    }

    private void Pump()
    {
        while (!_stopping)
        {
            if (_job.Wait(PumpTimeout) is not { Kind: not (JobMessageKind.Wake or JobMessageKind.Other) } message)
            {
                continue;
            }

            int id = message.ProcessId;
            DateTimeOffset now = _time.GetUtcNow();
            JobEvent jobEvent = message.Kind switch
            {
                JobMessageKind.NewProcess => new JobEvent(JobEventKind.Started, id, _images.GetOrAdd(id, ProcessNative.TryGetImagePath), now),
                JobMessageKind.ExitProcess => new JobEvent(JobEventKind.Exited, id, _images.GetValueOrDefault(id), now),
                JobMessageKind.AbnormalExitProcess => new JobEvent(JobEventKind.ExitedAbnormally, id, _images.GetValueOrDefault(id), now),
                _ => new JobEvent(JobEventKind.Empty, 0, null, now),
            };

            UninstallLog.JobEventSeen(_logger, jobEvent.Kind, id, jobEvent.ImagePath);
            _events.Writer.TryWrite(jobEvent);
        }

        _events.Writer.TryComplete();
    }
}
