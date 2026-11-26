using Microsoft.Extensions.Logging;
using Niskala.Interop.Processes;
using Niskala.Uninstall.Execution;

namespace Niskala.Uninstall;

internal static partial class UninstallLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Started process {ProcessId} in a job: {CommandLine}")]
    public static partial void ProcessStarted(ILogger logger, int processId, string commandLine);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Could not start {CommandLine}: Win32 error {Error}")]
    public static partial void ProcessStartFailed(ILogger logger, string commandLine, int error);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Job event {Kind}: process {ProcessId} {ImagePath}")]
    public static partial void JobEventSeen(ILogger logger, JobEventKind kind, int processId, string? imagePath);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Job process list query failed: Win32 error {Error}")]
    public static partial void JobQueryFailed(ILogger logger, int error);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Completion phase {From} -> {To}, {Processes} processes in the job")]
    public static partial void PhaseChanged(ILogger logger, CompletionPhase from, CompletionPhase to, int processes);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Information, Message = "Uninstall finished: {Reason}, exit code {ExitCode}, {ElapsedMs} ms, {Tail} processes left")]
    public static partial void UninstallFinished(ILogger logger, CompletionReason reason, int? exitCode, long elapsedMs, int tail);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "{AppId}: preflight can proceed {CanProceed}, {Issues} issues")]
    public static partial void PreflightDone(ILogger logger, string appId, bool canProceed, int issues);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Debug, Message = "Could not read permissions of {Path}")]
    public static partial void AclUnreadable(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Debug, Message = "Could not watch {Folder}")]
    public static partial void WatchFailed(ILogger logger, string folder, Exception exception);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "Package {Package} removed in {ElapsedMs} ms")]
    public static partial void PackageRemoved(ILogger logger, string package, long elapsedMs);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Error, Message = "Package {Package} was not removed: HRESULT {HResult} {ErrorText}")]
    public static partial void PackageRemovalFailed(ILogger logger, string package, int hResult, string? errorText);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Information, Message = "{AppId}: uninstall outcome {Outcome}, exit code {ExitCode} ({Meaning})")]
    public static partial void UninstallOutcome(ILogger logger, string appId, UninstallOutcome outcome, int? exitCode, ExitMeaning meaning);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Information, Message = "Process {ProcessId} {ImagePath}: {Outcome} (Win32 error {Error})")]
    public static partial void ProcessTerminated(ILogger logger, int processId, string? imagePath, TerminateOutcome outcome, int error);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Information, Message = "Service {Name} stop: Win32 error {Error}")]
    public static partial void ServiceStopped(ILogger logger, string name, int error);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Information, Message = "Steam app {AppId}: steam://uninstall handed over, Win32 error {Error}")]
    public static partial void SteamHandedOver(ILogger logger, int appId, int error);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Information, Message = "Steam asked to exit: closed {Closed}")]
    public static partial void SteamClosed(ILogger logger, bool closed);
}
