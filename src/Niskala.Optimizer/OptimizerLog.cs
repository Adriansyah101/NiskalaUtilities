using Microsoft.Extensions.Logging;
using Niskala.Optimizer.Processes;

namespace Niskala.Optimizer;

internal static partial class OptimizerLog
{
    [LoggerMessage(EventId = 10001, Level = LogLevel.Information, Message = "Process {ProcessId} ({Name}) restrained: {Kind}")]
    public static partial void RestraintApplied(ILogger logger, int processId, string name, RestraintKind kind);

    [LoggerMessage(EventId = 10002, Level = LogLevel.Information, Message = "Process {ProcessId} released, error {Error}")]
    public static partial void RestraintUndone(ILogger logger, int processId, int error);

    [LoggerMessage(EventId = 10003, Level = LogLevel.Information, Message = "Process {ProcessId} capped at {Percent}% CPU, error {Error}")]
    public static partial void CpuCapped(ILogger logger, int processId, int percent, int error);
}
