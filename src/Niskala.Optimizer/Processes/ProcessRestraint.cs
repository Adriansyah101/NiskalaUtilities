using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Safety;
using Niskala.Interop.Processes;

namespace Niskala.Optimizer.Processes;

public enum ProcessRefusal
{
    None,
    SystemProcess,
    OtherSession,
    Critical,
    CriticalUnknown,
    WindowsComponent,
    Niskala,
    NiskalaAncestor,
    ImageUnknown,
    Exited,
}

public enum RestraintKind
{
    Suspended,
    Efficiency,
}

public sealed record ProcessCandidate(int Id, string Name, string? ImagePath, bool HasWindow, double CpuPercent, ProcessRefusal Refusal)
{
    public const double IdleCpuPercent = 0.5;

    public RestraintKind? Restraint { get; init; }

    public bool CanRestrain => Refusal == ProcessRefusal.None;

    public bool IsIdleBackground => !HasWindow && CpuPercent < IdleCpuPercent;
}

public sealed record Restraint(int ProcessId, DateTime StartedUtc, string Name, string? ImagePath, RestraintKind Kind, PriorityClass? PreviousPriority, DateTimeOffset AppliedAt);

public enum RestraintOutcome
{
    Applied,
    Undone,
    Refused,
    NotRestrained,
    Failed,
}

public sealed record RestraintResult(int ProcessId, RestraintOutcome Outcome, ProcessRefusal Refusal = ProcessRefusal.None, int Error = 0);

public sealed class ProcessGuard(IProcessSystem system)
{
    private const int IdleProcessId = 0;
    private const int SystemProcessId = 4;
    private const int ServiceSession = 0;
    private const int AncestorDepth = 64;

    public ProcessRefusal Check(int processId, IReadOnlyList<ProcessEntry>? snapshot = null)
    {
        snapshot ??= system.Snapshot();
        if (processId is IdleProcessId or SystemProcessId)
        {
            return ProcessRefusal.SystemProcess;
        }

        if (processId == system.CurrentProcessId)
        {
            return ProcessRefusal.Niskala;
        }

        if (!snapshot.Any(p => p.Id == processId))
        {
            return ProcessRefusal.Exited;
        }

        if (Ancestors(snapshot).Contains(processId))
        {
            return ProcessRefusal.NiskalaAncestor;
        }

        int? session = system.SessionId(processId);
        int? own = system.SessionId(system.CurrentProcessId);
        if (session is null or ServiceSession || session != own)
        {
            return ProcessRefusal.OtherSession;
        }

        if (system.ImagePath(processId) is not { } image)
        {
            return ProcessRefusal.ImageUnknown;
        }

        if (PathSyntax.IsSameOrUnder(image, system.WindowsDirectory, out _))
        {
            return ProcessRefusal.WindowsComponent;
        }

        if (Path.GetFileName(image).StartsWith("niskala", StringComparison.OrdinalIgnoreCase)
            || system.OwnDirectories.Any(d => PathSyntax.IsSameOrUnder(image, d, out _)))
        {
            return ProcessRefusal.Niskala;
        }

        return system.IsCritical(processId) switch
        {
            null => ProcessRefusal.CriticalUnknown,
            true => ProcessRefusal.Critical,
            false => ProcessRefusal.None,
        };
    }

    private HashSet<int> Ancestors(IReadOnlyList<ProcessEntry> snapshot)
    {
        Dictionary<int, int> parents = snapshot.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().ParentId);
        var ancestors = new HashSet<int>();
        int current = system.CurrentProcessId;
        for (int depth = 0; depth < AncestorDepth && parents.TryGetValue(current, out int parent) && parent > SystemProcessId && ancestors.Add(parent); depth++)
        {
            current = parent;
        }

        return ancestors;
    }
}

public sealed class RestraintStore(string file)
{
    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "restraints.json");

    public IReadOnlyList<Restraint> Load()
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            using FileStream stream = File.OpenRead(file);
            return JsonSerializer.Deserialize(stream, RestraintJsonContext.Default.ListRestraint) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<Restraint> restraints)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, restraints.ToList(), RestraintJsonContext.Default.ListRestraint);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

public sealed class ProcessRestrainer(IProcessSystem system, ProcessGuard guard, RestraintStore store, ILogger<ProcessRestrainer> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public async Task<IReadOnlyList<ProcessCandidate>> ListAsync(TimeSpan sample, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessEntry> snapshot = system.Snapshot();
        Dictionary<int, TimeSpan> before = snapshot.Select(p => (p.Id, Times: system.Times(p.Id))).Where(t => t.Times is not null).ToDictionary(t => t.Id, t => t.Times!.Value.Cpu);
        long start = _time.GetTimestamp();
        if (sample > TimeSpan.Zero)
        {
            await Task.Delay(sample, _time, cancellationToken).ConfigureAwait(false);
        }

        double elapsed = Math.Max(_time.GetElapsedTime(start).TotalMilliseconds, 1) * Environment.ProcessorCount;
        IReadOnlySet<int> windowed = system.ProcessesWithVisibleWindows();
        Dictionary<int, Restraint> restrained = Current().ToDictionary(r => r.ProcessId);
        var candidates = new List<ProcessCandidate>();
        foreach (ProcessEntry process in snapshot)
        {
            double cpu = before.TryGetValue(process.Id, out TimeSpan first) && system.Times(process.Id) is { } after
                ? Math.Max(0, (after.Cpu - first).TotalMilliseconds) * 100 / elapsed
                : 0;
            candidates.Add(new ProcessCandidate(process.Id, process.ExeName, system.ImagePath(process.Id), windowed.Contains(process.Id), cpu, guard.Check(process.Id, snapshot))
            {
                Restraint = restrained.TryGetValue(process.Id, out Restraint? restraint) ? restraint.Kind : null,
            });
        }

        return [.. candidates.OrderBy(c => c.Refusal != ProcessRefusal.None).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<Restraint> Current()
    {
        lock (_gate)
        {
            IReadOnlyList<Restraint> saved = store.Load();
            List<Restraint> alive = [.. saved.Where(IsSameProcess)];
            if (alive.Count != saved.Count)
            {
                store.Save(alive);
            }

            return alive;
        }
    }

    public RestraintResult Suspend(int processId) => Apply(processId, RestraintKind.Suspended);

    public RestraintResult SetEfficiency(int processId) => Apply(processId, RestraintKind.Efficiency);

    public RestraintResult Undo(int processId)
    {
        lock (_gate)
        {
            List<Restraint> all = [.. store.Load().Where(IsSameProcess)];
            List<Restraint> mine = [.. all.Where(r => r.ProcessId == processId)];
            if (mine.Count == 0)
            {
                return new RestraintResult(processId, RestraintOutcome.NotRestrained);
            }

            int error = 0;
            foreach (Restraint restraint in mine)
            {
                int undone = restraint.Kind == RestraintKind.Suspended
                    ? system.Resume(processId)
                    : Restore(restraint);
                error = error == 0 ? undone : error;
            }

            if (error == 0)
            {
                all.RemoveAll(r => r.ProcessId == processId);
            }

            store.Save(all);
            OptimizerLog.RestraintUndone(logger, processId, error);
            return new RestraintResult(processId, error == 0 ? RestraintOutcome.Undone : RestraintOutcome.Failed, Error: error);
        }
    }

    public RestraintResult Cap(int processId, int percent, out CpuCapJob? job)
    {
        job = null;
        ProcessRefusal refusal = guard.Check(processId);
        if (refusal != ProcessRefusal.None)
        {
            return new RestraintResult(processId, RestraintOutcome.Refused, refusal);
        }

        int error = CpuCapJob.TryCreate(processId, percent, out job);
        OptimizerLog.CpuCapped(logger, processId, percent, error);
        return new RestraintResult(processId, error == 0 ? RestraintOutcome.Applied : RestraintOutcome.Failed, Error: error);
    }

    public IReadOnlyList<RestraintResult> UndoAll() => [.. Current().Select(r => r.ProcessId).Distinct().Select(Undo)];

    private RestraintResult Apply(int processId, RestraintKind kind)
    {
        lock (_gate)
        {
            ProcessRefusal refusal = guard.Check(processId);
            if (refusal != ProcessRefusal.None)
            {
                return new RestraintResult(processId, RestraintOutcome.Refused, refusal);
            }

            List<Restraint> all = [.. store.Load().Where(IsSameProcess)];
            if (all.Any(r => r.ProcessId == processId && r.Kind == kind))
            {
                return new RestraintResult(processId, RestraintOutcome.Applied);
            }

            if (system.Times(processId) is not { } times)
            {
                return new RestraintResult(processId, RestraintOutcome.Refused, ProcessRefusal.Exited);
            }

            PriorityClass? previous = kind == RestraintKind.Efficiency ? system.Priority(processId) : null;
            int error = kind == RestraintKind.Suspended
                ? system.Suspend(processId)
                : Efficient(processId);
            if (error != 0)
            {
                return new RestraintResult(processId, RestraintOutcome.Failed, Error: error);
            }

            string name = system.Snapshot().FirstOrDefault(p => p.Id == processId).ExeName ?? string.Empty;
            all.Add(new Restraint(processId, times.StartedUtc, name, system.ImagePath(processId), kind, previous, _time.GetUtcNow()));
            store.Save(all);
            OptimizerLog.RestraintApplied(logger, processId, name, kind);
            return new RestraintResult(processId, RestraintOutcome.Applied);
        }
    }

    private int Efficient(int processId)
    {
        int error = system.SetEfficiency(processId, efficient: true);
        return error != 0 ? error : system.SetPriority(processId, PriorityClass.Idle);
    }

    private int Restore(Restraint restraint)
    {
        int error = system.SetEfficiency(restraint.ProcessId, efficient: false);
        int priority = system.SetPriority(restraint.ProcessId, restraint.PreviousPriority ?? PriorityClass.Normal);
        return error != 0 ? error : priority;
    }

    private bool IsSameProcess(Restraint restraint) => system.Times(restraint.ProcessId) is { } times && times.StartedUtc == restraint.StartedUtc;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<Restraint>))]
internal sealed partial class RestraintJsonContext : JsonSerializerContext;
