using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Uninstall.Commands;

namespace Niskala.Uninstall.Preflight;

public enum PreflightCheck
{
    Protected,
    Command,
    NoRemove,
    Elevation,
    InstallerBusy,
    AppRunning,
}

public enum IssueSeverity
{
    Warning,
    Blocking,
}

public sealed record PreflightIssue(PreflightCheck Check, IssueSeverity Severity, string Message);

public sealed record PreflightReport(
    bool IsElevated,
    bool InstallerBusy,
    IReadOnlyList<RunningProcess> AppProcesses,
    IReadOnlyList<PreflightIssue> Issues)
{
    public bool CanProceed => Issues.All(issue => issue.Severity != IssueSeverity.Blocking);
}

public sealed class UninstallPreflight(IPreflightEnvironment environment, PathGuard guard, ILogger<UninstallPreflight> logger)
{
    public PreflightReport Run(InstalledApp app, CommandResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(resolution);
        var issues = new List<PreflightIssue>();

        if (app.IsProtected)
        {
            issues.Add(new(PreflightCheck.Protected, IssueSeverity.Blocking, "Removing this entry would break Windows."));
        }

        if (!resolution.CanRun)
        {
            issues.Add(new(PreflightCheck.Command, IssueSeverity.Blocking, $"No command to run: {resolution.Issue}."));
        }
        else if (resolution.Issue == ResolutionIssue.SilentUnsupported)
        {
            issues.Add(new(PreflightCheck.Command, IssueSeverity.Warning, "No silent switch is known for this installer; it will show its own UI."));
        }

        if (app.NoRemove)
        {
            issues.Add(new(PreflightCheck.NoRemove, IssueSeverity.Warning, "The publisher marks this entry as not removable (NoRemove)."));
        }

        bool elevated = environment.IsElevated;
        if (app.Scope == InstallScope.Machine && !elevated)
        {
            issues.Add(new(PreflightCheck.Elevation, IssueSeverity.Blocking, "A machine-wide uninstall needs an elevated process."));
        }

        bool busy = environment.IsInstallerBusy();
        if (busy)
        {
            bool needsInstaller = app.InstallerKind is InstallerKind.Msi or InstallerKind.WixBurn
                || resolution.Command?.Origin == CommandOrigin.WindowsInstaller;
            issues.Add(new(
                PreflightCheck.InstallerBusy,
                needsInstaller ? IssueSeverity.Blocking : IssueSeverity.Warning,
                "Windows Installer is running another installation (Global\\_MSIExecute is held)."));
        }

        IReadOnlyList<RunningProcess> running = AppProcesses(app);
        if (running.Count > 0)
        {
            string names = string.Join(", ", running.Select(p => $"{p.Name} ({p.Id})"));
            issues.Add(new(PreflightCheck.AppRunning, IssueSeverity.Warning, $"{running.Count} processes from the install folder are running: {names}."));
        }

        var report = new PreflightReport(elevated, busy, running, issues);
        UninstallLog.PreflightDone(logger, app.Id, report.CanProceed, issues.Count);
        return report;
    }

    private IReadOnlyList<RunningProcess> AppProcesses(InstalledApp app)
    {
        IReadOnlyList<string> roots = app.InstallLocation is null ? [] : guard.Check(app.InstallLocation).AllowedForms;
        if (roots.Count == 0)
        {
            return [];
        }

        return [.. environment.RunningProcesses().Where(p =>
            p.ImagePath is not null
            && PathSyntax.TryNormalize(p.ImagePath, out string image) == PathVerdict.Allowed
            && roots.Any(root => PathSyntax.IsSameOrUnder(image, root, out _)))];
    }
}
