using Microsoft.Extensions.Logging;
using Niskala.Core.Diagnostics;
using Niskala.Host;
using Niskala.Optimizer.Drives;
using Niskala.UI.Services;

namespace Niskala.UI;

public static class Program
{
    public const string ScheduledLogPrefix = "niskala-scheduled";

    [STAThread]
    public static int Main(string[] args)
    {
        if (ScheduledMaintenance.IsScheduledRun(args))
        {
            return ScheduledRun.Execute();
        }

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }
}

internal static partial class ScheduledRun
{
    public static int Execute()
    {
        using var single = new Mutex(initiallyOwned: true, ScheduledOptimization.MutexName, out bool owned);
        if (!owned)
        {
            return 0;
        }

        using ILoggerFactory factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new JsonLinesFileLoggerProvider(JsonLinesFileLoggerProvider.DefaultDirectory, Program.ScheduledLogPrefix));
        });
        ILogger logger = factory.CreateLogger(nameof(ScheduledRun));
        try
        {
            IReadOnlyList<QueueResult> results = ScheduledOptimization.RunAsync(NiskalaHost.CreateDriveOptimizer(), null, CancellationToken.None).GetAwaiter().GetResult();
            foreach (QueueResult result in results)
            {
                Finished(logger, result.Step.Volume.Name, result.Step.Kind, result.Outcome);
            }

            return ScheduledOptimization.ExitCode(results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Failed(logger, ex);
            return 1;
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    [LoggerMessage(EventId = 8005, Level = LogLevel.Information, Message = "Scheduled optimization of {Drive}: {Step} {Outcome}")]
    private static partial void Finished(ILogger logger, string drive, QueueStepKind step, QueueOutcome outcome);

    [LoggerMessage(EventId = 8006, Level = LogLevel.Warning, Message = "Scheduled optimization failed")]
    private static partial void Failed(ILogger logger, Exception exception);
}
