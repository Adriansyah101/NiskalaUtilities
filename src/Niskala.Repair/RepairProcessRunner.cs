using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Repair.Output;

namespace Niskala.Repair;

public enum RepairStream
{
    Output,
    Error,
}

public readonly record struct RepairUpdate(RepairStream Stream, string Text, bool IsFinal, double? Percent);

public sealed record RepairResult(
    RepairCommandId Command,
    RepairOutcome Outcome,
    RepairDiagnostic Diagnostic,
    int? ExitCode,
    int? StartError,
    double? LastPercent,
    TimeSpan Elapsed)
{
    public bool Succeeded => Outcome is RepairOutcome.Succeeded or RepairOutcome.RestartRequired;

    public bool RanToEnd => Succeeded || Outcome == RepairOutcome.IssuesFound;

    public double FinalPercent => RanToEnd ? RepairProgressParser.Complete : Math.Clamp(LastPercent ?? 0, 0, RepairProgressParser.Complete);
}

internal sealed record ProcessRun(int? ExitCode, int? StartError, bool Cancelled, double? LastPercent);

public sealed class RepairProcessRunner(IRepairEnvironment environment, ILogger<RepairProcessRunner> logger, TimeProvider? time = null)
{
    private const int ChunkBytes = 4096;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<RepairResult> RunAsync(RepairCommandId id, IProgress<RepairUpdate>? progress = null, CancellationToken cancellationToken = default)
    {
        RepairCommand command = RepairCommandCatalog.Get(id);
        if (command.NeedsElevation && !environment.IsElevated)
        {
            RepairLog.NotElevated(logger, command.Name);
            return new RepairResult(id, RepairOutcome.NotElevated, RepairDiagnostic.ElevationRequired, null, null, null, TimeSpan.Zero);
        }

        RepairInvocation invocation = RepairCommandCatalog.Resolve(id, environment);
        long start = _time.GetTimestamp();
        RepairLog.Started(logger, command.Name, invocation.CommandLine);
        ProcessRun run = await RunProcessAsync(invocation.FileName, invocation.Arguments, progress, cancellationToken).ConfigureAwait(false);
        TimeSpan elapsed = _time.GetElapsedTime(start);
        (RepairOutcome outcome, RepairDiagnostic diagnostic) = run switch
        {
            { Cancelled: true } => (RepairOutcome.Cancelled, RepairDiagnostic.None),
            { StartError: RepairExitCodes.FileNotFound } => (RepairOutcome.StartFailed, RepairDiagnostic.ProgramMissing),
            { StartError: not null } => (RepairOutcome.StartFailed, RepairDiagnostic.None),
            _ => RepairExitCodes.Interpret(command, run.ExitCode ?? RepairExitCodes.Success),
        };

        RepairLog.Finished(logger, command.Name, outcome, run.ExitCode ?? run.StartError, (long)elapsed.TotalMilliseconds);
        return new RepairResult(id, outcome, diagnostic, run.ExitCode, run.StartError, run.LastPercent, elapsed);
    }

    internal async Task<ProcessRun> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, IProgress<RepairUpdate>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessRun(null, null, Cancelled: true, null);
        }

        var file = new FileInfo(fileName);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            RepairLog.ProgramMissing(logger, fileName);
            return new ProcessRun(null, RepairExitCodes.FileNotFound, Cancelled: false, null);
        }

        var info = new ProcessStartInfo(file.FullName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = file.DirectoryName,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            RepairLog.StartFailed(logger, fileName, ex.NativeErrorCode, ex);
            return new ProcessRun(null, ex.NativeErrorCode, Cancelled: false, null);
        }

        process.StandardInput.Close();
        var percent = new PercentTracker();
        Task output = PumpAsync(process.StandardOutput.BaseStream, RepairStream.Output, progress, percent);
        Task error = PumpAsync(process.StandardError.BaseStream, RepairStream.Error, progress, percent);

        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(output, error).WaitAsync(DrainTimeout, _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RepairLog.OutputStillOpen(logger, fileName);
        }

        return new ProcessRun(cancelled ? null : process.ExitCode, null, cancelled, percent.Last);
    }

    private async Task PumpAsync(Stream stream, RepairStream kind, IProgress<RepairUpdate>? progress, PercentTracker percent)
    {
        var decoder = new ConsoleTextDecoder(environment.OemCodePage);
        var assembler = new ConsoleLineAssembler();
        var reader = new ProgressReader();
        var lines = new List<ConsoleLine>();
        byte[] buffer = new byte[ChunkBytes];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                assembler.Append(decoder.Decode(buffer.AsSpan(0, read)), lines);
                Publish();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            RepairLog.OutputClosed(logger, kind, ex);
        }

        assembler.Append(decoder.Finish(), lines);
        if (assembler.Complete() is { } last)
        {
            lines.Add(last);
        }

        Publish();

        void Publish()
        {
            foreach (ConsoleLine line in lines)
            {
                double? value = reader.Read(line);
                if (value is not null)
                {
                    percent.Last = value;
                }

                progress?.Report(new RepairUpdate(kind, line.Text, line.IsFinal, value));
            }

            lines.Clear();
        }
    }

    private void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            RepairLog.StopFailed(logger, process.Id, ex);
        }
    }

    private sealed class PercentTracker
    {
        private readonly Lock _gate = new();
        private double? _last;

        public double? Last
        {
            get
            {
                lock (_gate)
                {
                    return _last;
                }
            }

            set
            {
                lock (_gate)
                {
                    _last = value;
                }
            }
        }
    }
}

internal static partial class RepairLog
{
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Repair {Command} started: {CommandLine}")]
    public static partial void Started(ILogger logger, string command, string commandLine);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Repair {Command} finished: {Outcome}, code {Code}, {ElapsedMs} ms")]
    public static partial void Finished(ILogger logger, string command, RepairOutcome outcome, int? code, long elapsedMs);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Repair {Command} needs an elevated process")]
    public static partial void NotElevated(ILogger logger, string command);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Error, Message = "{Program} is missing or is a link")]
    public static partial void ProgramMissing(ILogger logger, string program);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Error, Message = "{Program} did not start (Win32 error {Error})")]
    public static partial void StartFailed(ILogger logger, string program, int error, Exception exception);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Process {ProcessId} and its children could not all be stopped")]
    public static partial void StopFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Debug, Message = "The {Stream} pipe closed while it was read")]
    public static partial void OutputClosed(ILogger logger, RepairStream stream, Exception exception);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Warning, Message = "{Program} exited but a process it started still holds its output open")]
    public static partial void OutputStillOpen(ILogger logger, string program);
}
