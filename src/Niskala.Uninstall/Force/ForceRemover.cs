using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Interop.Processes;
using Niskala.Interop.Services;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Force;

public interface IProcessControl
{
    IReadOnlyList<RunningProcess> List();

    TerminateResult Terminate(RunningProcess process, TimeSpan wait);
}

public sealed class SystemProcessControl : IProcessControl
{
    public static SystemProcessControl Instance { get; } = new();

    public IReadOnlyList<RunningProcess> List() => SystemPreflightEnvironment.Instance.RunningProcesses();

    public TerminateResult Terminate(RunningProcess process, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process.ImagePath is { } image
            ? ProcessNative.Terminate(process.Id, image, wait)
            : new TerminateResult(TerminateOutcome.ImageChanged, 0);
    }
}

public sealed record ForceOptions
{
    public bool Permanent { get; init; }

    public bool CreateRestorePoint { get; init; } = true;

    public bool IncludeReview { get; init; }

    public IReadOnlyList<LeftoverItem>? Chosen { get; init; }
}

public sealed record ForcePlan(
    InstalledApp App,
    bool InInventory,
    Fingerprint Fingerprint,
    IReadOnlyList<RunningProcess> Processes,
    IReadOnlyList<ServiceTrace> Services,
    LeftoverReport Leftovers);

public sealed record ForceTarget(InstalledApp App, bool InInventory);

public sealed record ProcessStop(RunningProcess Process, TerminateResult Result);

public sealed record ServiceStop(string Name, int Error);

public sealed record ForceResult(IReadOnlyList<ProcessStop> Processes, IReadOnlyList<ServiceStop> Services, LeftoverReport Leftovers, CleanResult Clean);

public sealed class ForceRemover(
    FingerprintCollector fingerprints,
    LeftoverScanner scanner,
    LeftoverCleaner cleaner,
    IProcessControl processes,
    IServiceControl services,
    ILogger<ForceRemover> logger)
{
    private static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(30);

    public static InstalledApp ForFolder(string folder, IEnumerable<string> knownRoots)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(knownRoots);
        string? parent = Path.GetDirectoryName(folder);
        bool parentIsRoot = parent is null || Path.GetDirectoryName(parent) is null
            || knownRoots.Any(r => r.TrimEnd('\\').Equals(parent, StringComparison.OrdinalIgnoreCase));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new InstalledApp
        {
            Id = "folder:" + folder,
            Sources = AppSources.None,
            Scope = profile.Length > 0 && PathSyntax.IsSameOrUnder(folder, profile, out _) ? InstallScope.User : InstallScope.Machine,
            DisplayName = Path.GetFileName(folder),
            Publisher = parentIsRoot ? null : Path.GetFileName(parent),
            InstallLocation = folder,
            InstallLocationGuard = PathVerdict.Allowed,
        };
    }

    public static ForceTarget? ForPath(string path, PathGuard guard, IReadOnlyList<InstalledApp> installed, IEnumerable<string> knownRoots, out PathDecision decision)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(installed);
        decision = guard.Check(path);
        if (!decision.IsAllowed || !decision.Exists || !Directory.Exists(decision.CanonicalPath))
        {
            return null;
        }

        PathDecision allowed = decision;
        InstalledApp? owner = installed.FirstOrDefault(a => a.InstallLocationGuard == PathVerdict.Allowed
            && PathSyntax.TryNormalize(a.InstallLocation, out string location) == PathVerdict.Allowed
            && allowed.AllowedForms.Any(f => f.Equals(location, StringComparison.OrdinalIgnoreCase)));
        return owner is not null ? new ForceTarget(owner, true) : new ForceTarget(ForFolder(decision.CanonicalPath!, knownRoots), false);
    }

    public static IReadOnlyList<LeftoverItem> Select(InstalledApp app, LeftoverReport report, bool includeReview)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(report);
        return [.. report.Items.Where(i => i.Tier == ConfidenceTier.Safe
            || (includeReview && i.Tier == ConfidenceTier.Review)
            || (i.Kind == LeftoverKind.RegistryKey && i.Path.Equals(app.RegistryKey, StringComparison.OrdinalIgnoreCase)))];
    }

    public ForcePlan Plan(InstalledApp app, bool inInventory, IReadOnlyList<InstalledApp> installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(installed);
        Fingerprint fingerprint = fingerprints.Collect(app, cancellationToken: cancellationToken);
        LeftoverReport report = scanner.Scan(app, fingerprint, installed, cancellationToken);
        return new ForcePlan(app, inInventory, fingerprint, Running(fingerprint.Roots), fingerprint.Services, report);
    }

    public async Task<ForceResult> ExecuteAsync(
        ForcePlan plan,
        IReadOnlyList<InstalledApp> installed,
        ForceOptions options,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(options);

        var stopped = new List<ProcessStop>();
        foreach (RunningProcess process in Running(plan.Fingerprint.Roots))
        {
            TerminateResult result = processes.Terminate(process, TerminateWait);
            UninstallLog.ProcessTerminated(logger, process.Id, process.ImagePath, result.Outcome, result.Error);
            stopped.Add(new ProcessStop(process, result));
        }

        var halted = new List<ServiceStop>();
        foreach (ServiceTrace service in plan.Services)
        {
            int error = services.TryQuery(service.Name, out ServiceState state);
            if (error == 0 && state is not (ServiceState.NotFound or ServiceState.Stopped))
            {
                error = services.TryStop(service.Name, ServiceStopTimeout);
            }

            UninstallLog.ServiceStopped(logger, service.Name, error);
            halted.Add(new ServiceStop(service.Name, error));
        }

        cancellationToken.ThrowIfCancellationRequested();
        LeftoverReport report = scanner.Scan(plan.App, plan.Fingerprint, installed, cancellationToken);
        IReadOnlyList<LeftoverItem> items = options.Chosen is { } chosen ? Pick(report, chosen) : Select(plan.App, report, options.IncludeReview);
        var cleanOptions = new CleanOptions { Kind = TransactionKind.Force, Permanent = options.Permanent, CreateRestorePoint = options.CreateRestorePoint };
        CleanResult clean = await cleaner.CleanAsync(plan.App, plan.Fingerprint, items, cleanOptions, progress, cancellationToken).ConfigureAwait(false);
        return new ForceResult(stopped, halted, report, clean);
    }

    private static LeftoverItem[] Pick(LeftoverReport report, IReadOnlyList<LeftoverItem> chosen) =>
        [.. chosen.Where(c => report.Items.Any(i => LeftoverTree.Contains(i, c)))];

    private List<RunningProcess> Running(IReadOnlyList<string> roots) => roots.Count == 0
        ? []
        : [.. processes.List().Where(p => p.Id != Environment.ProcessId
            && PathSyntax.TryNormalize(p.ImagePath, out string image) == PathVerdict.Allowed
            && roots.Any(r => PathSyntax.IsSameOrUnder(image, r, out _)))];
}
