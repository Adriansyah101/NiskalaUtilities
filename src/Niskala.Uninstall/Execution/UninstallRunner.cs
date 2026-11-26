using Microsoft.Extensions.Logging;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Execution;

public sealed record RunnerResult(int StartError, UninstallRun? Run);

public interface IUninstallRunner
{
    Task<RunnerResult> RunAsync(
        ResolvedCommand command,
        string? registryKey,
        IReadOnlyList<string> installRoots,
        IProgress<JobEvent>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class JobUninstallRunner(CompletionOptions options, ILoggerFactory loggerFactory) : IUninstallRunner
{
    public async Task<RunnerResult> RunAsync(
        ResolvedCommand command,
        string? registryKey,
        IReadOnlyList<string> installRoots,
        IProgress<JobEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(installRoots);

        using var activity = new UninstallActivityProbe(
            registryKey,
            installRoots.Count == 0 ? null : installRoots[^1],
            loggerFactory.CreateLogger<UninstallActivityProbe>());

        int error = JobTracker.TryStart(command, loggerFactory.CreateLogger<JobTracker>(), out JobTracker? tracker);
        if (error != 0)
        {
            return new RunnerResult(error, null);
        }

        using (tracker)
        {
            var monitor = new CompletionMonitor(options, WindowsInstallerMutex.IsHeld, loggerFactory.CreateLogger<CompletionMonitor>());
            UninstallRun run = await monitor.WaitAsync(tracker!, activity, installRoots, progress, cancellationToken).ConfigureAwait(false);
            return new RunnerResult(0, run);
        }
    }
}
