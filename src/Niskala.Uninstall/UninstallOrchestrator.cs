using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Guardian;
using Niskala.Scanner.Fingerprints;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Packages;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Verification;

namespace Niskala.Uninstall;

public sealed record UninstallPlan(InstalledApp App, CommandResolution Resolution, PreflightReport Preflight, Fingerprint Fingerprint);

public enum UninstallStage
{
    Guardian,
    Running,
    Verifying,
}

public sealed record UninstallProgress(UninstallStage Stage, JobEvent? Event = null);

public enum UninstallOutcome
{
    Blocked,
    NotStarted,
    Removed,
    PendingReboot,
    Cancelled,
    Failed,
    StaleEntry,
    StillInstalled,
    Unverified,
}

public sealed record UninstallVerification(bool? KeyPresent, bool? ProductRegistered, bool? PackagePresent, FolderState? Folder)
{
    public bool? StillRegistered => (KeyPresent, ProductRegistered, PackagePresent) switch
    {
        (null, null, null) => null,
        _ => KeyPresent == true || ProductRegistered == true || PackagePresent == true,
    };
}

public sealed record UninstallResult(
    UninstallOutcome Outcome,
    PreflightReport Preflight,
    GuardianCheckpoint? Checkpoint,
    int StartError,
    int? ExitCode,
    ExitMeaning ExitMeaning,
    UninstallRun? Run,
    PackageRemovalResult? Package,
    UninstallVerification? Verification,
    string? FingerprintFile,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes)
{
    public bool RebootRequired => ExitMeaning is ExitMeaning.RebootRequired or ExitMeaning.RebootInitiated;
}

public sealed class UninstallOrchestrator(
    CommandLineResolver resolver,
    UninstallPreflight preflight,
    FingerprintCollector fingerprints,
    FingerprintStore store,
    IGuardianService guardian,
    IUninstallRunner runner,
    IPackageRemover packages,
    IInstallStateProbe state,
    ILogger<UninstallOrchestrator> logger)
{
    private const int ErrorElevationRequired = 740;

    public UninstallPlan Plan(InstalledApp app, UninstallMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandResolution resolution = resolver.Resolve(app, mode);
        PreflightReport report = preflight.Run(app, resolution);
        Fingerprint fingerprint = fingerprints.Collect(app, cancellationToken: cancellationToken);
        return new UninstallPlan(app, resolution, report, fingerprint);
    }

    public async Task<UninstallResult> ExecuteAsync(UninstallPlan plan, IProgress<UninstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long start = Stopwatch.GetTimestamp();
        InstalledApp app = plan.App;
        var notes = new List<string>();

        PreflightReport report = preflight.Run(app, plan.Resolution);
        if (!report.CanProceed)
        {
            return Finish(new UninstallResult(UninstallOutcome.Blocked, report, null, 0, null, ExitMeaning.Unknown, null, null, null, null, Stopwatch.GetElapsedTime(start), notes));
        }

        string? fingerprintFile = store.Save(app, plan.Fingerprint);
        if (fingerprintFile is null)
        {
            notes.Add("The fingerprint could not be saved; a later leftover scan has less evidence.");
        }

        progress?.Report(new UninstallProgress(UninstallStage.Guardian));
        GuardianCheckpoint checkpoint = await guardian.PrepareAsync(app, plan.Fingerprint, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UninstallProgress(UninstallStage.Running));
        int startError = 0;
        int? exitCode = null;
        ExitMeaning meaning;
        UninstallRun? run = null;
        PackageRemovalResult? removal = null;
        if (plan.Resolution.PackageFullName is { } packageName)
        {
            removal = await packages.RemoveAsync(packageName, cancellationToken).ConfigureAwait(false);
            exitCode = removal.HResult;
            meaning = removal.Succeeded ? ExitMeaning.Success : ExitMeaning.Failed;
        }
        else
        {
            ResolvedCommand command = plan.Resolution.Command!;
            IProgress<JobEvent>? events = progress is null ? null : new EventForwarder(progress);
            RunnerResult result = await runner.RunAsync(command, app.RegistryKey, plan.Fingerprint.Roots, events, cancellationToken).ConfigureAwait(false);
            if (result.StartError != 0)
            {
                startError = result.StartError;
                notes.Add(startError == ErrorElevationRequired
                    ? "The uninstaller demands elevation; run Niskala elevated."
                    : $"The uninstaller could not be started (Win32 error {startError}).");
                return Finish(new UninstallResult(UninstallOutcome.NotStarted, report, checkpoint, startError, null, ExitMeaning.Unknown, null, null, null, fingerprintFile, Stopwatch.GetElapsedTime(start), notes));
            }

            run = result.Run!;
            exitCode = run.ExitCode;
            meaning = UninstallerExitCodes.Interpret(exitCode, app.InstallerKind, command.Origin);
            if (app.InstallerKind == InstallerKind.Nsis)
            {
                notes.Add("NSIS relaunches its uninstaller from %TEMP%; the exit code is the launcher's.");
            }
        }

        progress?.Report(new UninstallProgress(UninstallStage.Verifying));
        UninstallVerification verification = Verify(app, plan);
        UninstallOutcome outcome = Decide(meaning, verification.StillRegistered);
        if (outcome == UninstallOutcome.Removed && meaning == ExitMeaning.Failed)
        {
            notes.Add($"The entry is gone although the uninstaller returned {exitCode}.");
        }

        if (verification.Folder is { Exists: true, Files: > 0 } folder)
        {
            notes.Add($"{folder.Files}{(folder.Truncated ? "+" : string.Empty)} files are left in the install folder.");
        }

        return Finish(new UninstallResult(outcome, report, checkpoint, startError, exitCode, meaning, run, removal, verification, fingerprintFile, Stopwatch.GetElapsedTime(start), notes));

        UninstallResult Finish(UninstallResult result)
        {
            UninstallLog.UninstallOutcome(logger, app.Id, result.Outcome, result.ExitCode, result.ExitMeaning);
            return result;
        }
    }

    internal static UninstallOutcome Decide(ExitMeaning meaning, bool? stillRegistered) => (stillRegistered, meaning) switch
    {
        (false, _) => UninstallOutcome.Removed,
        (_, ExitMeaning.Cancelled) => UninstallOutcome.Cancelled,
        (_, ExitMeaning.RebootRequired or ExitMeaning.RebootInitiated) => UninstallOutcome.PendingReboot,
        (_, ExitMeaning.ProductNotInstalled) => UninstallOutcome.StaleEntry,
        (_, ExitMeaning.Failed or ExitMeaning.InstallerBusy) => UninstallOutcome.Failed,
        (null, _) => UninstallOutcome.Unverified,
        _ => UninstallOutcome.StillInstalled,
    };

    private UninstallVerification Verify(InstalledApp app, UninstallPlan plan)
    {
        bool? key = app.RegistryKey is { } registryKey ? state.KeyExists(registryKey) : null;
        bool? product = app.MsiProductCode is { } code ? state.ProductRegistered(code) : null;
        bool? package = plan.Resolution.PackageFullName is { } name ? state.PackageExists(name) : null;
        FolderState? folder = plan.Fingerprint.Roots.Count == 0 ? null : state.Folder(plan.Fingerprint.Roots[^1]);
        return new UninstallVerification(key, product, package, folder);
    }

    private sealed class EventForwarder(IProgress<UninstallProgress> target) : IProgress<JobEvent>
    {
        public void Report(JobEvent value) => target.Report(new UninstallProgress(UninstallStage.Running, value));
    }
}
