using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Models;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;
using static Niskala.Uninstall.Tests.TestApps;

namespace Niskala.Uninstall.Tests;

public sealed class UninstallPreflightTests
{
    private static readonly CommandResolution Resolved = new(
        UninstallMode.Interactive,
        new ResolvedCommand(@"C:\Program Files\Acme\uninst.exe", string.Empty, CommandOrigin.UninstallString, false),
        ResolutionIssue.None,
        [],
        []);

    [Fact]
    public void Clean_machine_install_can_proceed()
    {
        PreflightReport report = Run(new FakePreflightEnvironment(), App(InstallerKind.Nsis));

        Assert.True(report.CanProceed);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Machine_install_needs_elevation_but_a_user_install_does_not()
    {
        var standardUser = new FakePreflightEnvironment(elevated: false);

        Assert.Equal(PreflightCheck.Elevation, Blocking(Run(standardUser, App(InstallerKind.Nsis))).Check);
        Assert.True(Run(standardUser, App(InstallerKind.Nsis) with { Scope = InstallScope.User }).CanProceed);
    }

    [Fact]
    public void Busy_windows_installer_blocks_only_installers_that_need_it()
    {
        var busy = new FakePreflightEnvironment(busy: true);

        PreflightReport msi = Run(busy, App(InstallerKind.Msi));
        PreflightReport nsis = Run(busy, App(InstallerKind.Nsis));

        Assert.True(msi.InstallerBusy);
        Assert.Equal(PreflightCheck.InstallerBusy, Blocking(msi).Check);
        Assert.True(nsis.CanProceed);
        Assert.Equal(IssueSeverity.Warning, Assert.Single(nsis.Issues).Severity);
    }

    [Fact]
    public void Running_processes_from_the_install_folder_are_listed()
    {
        var environment = new FakePreflightEnvironment(
            processes:
            [
                new RunningProcess(100, "acme.exe", @"C:\Program Files\Acme\bin\acme.exe"),
                new RunningProcess(101, "acmetools.exe", @"C:\Program Files\Acme Tools\acmetools.exe"),
                new RunningProcess(102, "protected.exe", null),
            ]);

        PreflightReport report = Run(environment, App(InstallerKind.Nsis));

        Assert.Equal(100, Assert.Single(report.AppProcesses).Id);
        Assert.Equal(PreflightCheck.AppRunning, Assert.Single(report.Issues).Check);
        Assert.True(report.CanProceed);
    }

    [Fact]
    public void Guard_denied_install_folder_never_claims_processes()
    {
        var environment = new FakePreflightEnvironment(processes: [new RunningProcess(100, "x.exe", @"C:\Program Files\x.exe")]);

        PreflightReport report = Run(environment, App(InstallerKind.Nsis) with { InstallLocation = @"C:\Program Files" });

        Assert.Empty(report.AppProcesses);
    }

    [Fact]
    public void Protected_entries_and_missing_commands_block()
    {
        var environment = new FakePreflightEnvironment();
        CommandResolution none = Resolved with { Command = null, Issue = ResolutionIssue.NoCommand };

        Assert.Equal(PreflightCheck.Protected, Blocking(Run(environment, App(InstallerKind.Msix) with { IsProtected = true })).Check);
        Assert.Equal(PreflightCheck.Command, Blocking(Run(environment, App(InstallerKind.Nsis), none)).Check);

        PreflightReport noRemove = Run(environment, App(InstallerKind.Nsis) with { NoRemove = true });
        Assert.True(noRemove.CanProceed);
        Assert.Equal(PreflightCheck.NoRemove, Assert.Single(noRemove.Issues).Check);
    }

    private static PreflightReport Run(IPreflightEnvironment environment, InstalledApp app, CommandResolution? resolution = null) =>
        new UninstallPreflight(environment, Guard, NullLogger<UninstallPreflight>.Instance).Run(app, resolution ?? Resolved);

    private static PreflightIssue Blocking(PreflightReport report)
    {
        Assert.False(report.CanProceed);
        return Assert.Single(report.Issues, issue => issue.Severity == IssueSeverity.Blocking);
    }
}
