using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Security;
using static Niskala.Uninstall.Tests.TestApps;

namespace Niskala.Uninstall.Tests;

public sealed class CommandLineResolverTests
{
    private const string Msiexec = @"C:\Windows\System32\msiexec.exe";
    private const string Uninstaller = @"C:\Program Files\Acme\uninst.exe";
    private const string Winget = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private static readonly Guid ProductCode = Guid.Parse("{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}");

    private readonly FakeEnvironment _environment = new(Msiexec, Uninstaller, Winget, @"C:\Windows\System32\cmd.exe");

    [Fact]
    public void Windows_installer_product_is_removed_with_msiexec_x_whatever_the_stored_string_says()
    {
        InstalledApp app = App(InstallerKind.Msi, "MsiExec.exe /I{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}", environment: _environment)
            with { MsiProductCode = ProductCode };

        CommandResolution interactive = Resolver().Resolve(app, UninstallMode.Interactive);
        CommandResolution silent = Resolver().Resolve(app, UninstallMode.Silent);

        Assert.Equal(new ResolvedCommand(Msiexec, "/x {5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}", CommandOrigin.WindowsInstaller, false), interactive.Command);
        Assert.Equal("/x {5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7} /qn /norestart", silent.Command?.Arguments);
        Assert.True(silent.Command?.IsSilent);
        Assert.Contains(interactive.Notes, note => note.Contains("/I", StringComparison.Ordinal));
        Assert.Equal(ResolutionIssue.None, silent.Issue);
    }

    [Fact]
    public void Msiexec_without_a_known_product_code_gets_x_instead_of_i()
    {
        InstalledApp app = App(InstallerKind.Msi, @"MsiExec.exe /I ""C:\Cache\acme.msi""", environment: _environment);

        CommandResolution silent = Resolver().Resolve(app, UninstallMode.Silent);

        Assert.Equal(@"/X ""C:\Cache\acme.msi"" /qn /norestart", silent.Command?.Arguments);
        Assert.Equal(CommandOrigin.UninstallString, silent.Command?.Origin);
    }

    [Theory]
    [InlineData(InstallerKind.Nsis, "", "/S")]
    [InlineData(InstallerKind.Nsis, "/s", "/s /S")]
    [InlineData(InstallerKind.Nsis, "/S", "/S")]
    [InlineData(InstallerKind.InnoSetup, "", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(InstallerKind.InnoSetup, "/silent", "/silent")]
    [InlineData(InstallerKind.WixBurn, "/uninstall", "/uninstall /quiet /norestart")]
    [InlineData(InstallerKind.WixBurn, "", "/uninstall /quiet /norestart")]
    [InlineData(InstallerKind.Squirrel, "--uninstall", "--uninstall -s")]
    public void Silent_mode_adds_the_installer_switches(InstallerKind kind, string stored, string expected)
    {
        InstalledApp app = App(kind, $@"""{Uninstaller}"" {stored}".TrimEnd(), environment: _environment);

        CommandResolution resolution = Resolver().Resolve(app, UninstallMode.Silent);

        Assert.Equal(expected, resolution.Command?.Arguments);
        Assert.True(resolution.Command?.IsSilent);
        Assert.Equal(ResolutionIssue.None, resolution.Issue);
    }

    [Fact]
    public void Interactive_mode_keeps_the_stored_arguments()
    {
        InstalledApp app = App(InstallerKind.Nsis, $@"""{Uninstaller}"" _?=C:\Program Files\Acme", environment: _environment);

        ResolvedCommand? command = Resolver().Resolve(app, UninstallMode.Interactive).Command;

        Assert.Equal(new ResolvedCommand(Uninstaller, @"_?=C:\Program Files\Acme", CommandOrigin.UninstallString, false), command);
        Assert.Equal($@"""{Uninstaller}"" _?=C:\Program Files\Acme", command?.CommandLine);
    }

    [Fact]
    public void Quiet_string_wins_in_silent_mode_and_serves_alone_when_nothing_else_exists()
    {
        InstalledApp both = App(InstallerKind.CustomExe, $@"""{Uninstaller}""", $@"""{Uninstaller}"" --quiet-mode", _environment);
        InstalledApp quietOnly = App(InstallerKind.CustomExe, quiet: $@"""{Uninstaller}"" --quiet-mode", environment: _environment);

        Assert.Equal(CommandOrigin.QuietUninstallString, Resolver().Resolve(both, UninstallMode.Silent).Command?.Origin);
        Assert.Equal(CommandOrigin.UninstallString, Resolver().Resolve(both, UninstallMode.Interactive).Command?.Origin);

        CommandResolution fallback = Resolver().Resolve(quietOnly, UninstallMode.Interactive);
        Assert.Equal(CommandOrigin.QuietUninstallString, fallback.Command?.Origin);
        Assert.True(fallback.Command?.IsSilent);
        Assert.Contains(fallback.Notes, note => note.Contains("quiet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallerKind.InstallShield, @"-runfromtemp -l0x0409 -removeonly")]
    [InlineData(InstallerKind.CustomExe, "")]
    public void Unknown_silent_switch_keeps_the_command_and_reports_it(InstallerKind kind, string stored)
    {
        InstalledApp app = App(kind, $@"""{Uninstaller}"" {stored}".TrimEnd(), environment: _environment);

        CommandResolution resolution = Resolver().Resolve(app, UninstallMode.Silent);

        Assert.Equal(ResolutionIssue.SilentUnsupported, resolution.Issue);
        Assert.Equal(stored, resolution.Command?.Arguments);
        Assert.False(resolution.Command?.IsSilent);
    }

    [Fact]
    public void Silent_switch_already_stored_is_detected()
    {
        InstalledApp app = App(InstallerKind.CustomExe, $@"""{Uninstaller}"" --silent", environment: _environment);

        CommandResolution resolution = Resolver().Resolve(app, UninstallMode.Interactive);

        Assert.True(resolution.Command?.IsSilent);
        Assert.True(CommandLineResolver.LooksSilent("/x {X} /QN", InstallerKind.Msi, runsMsiexec: true));
        Assert.False(CommandLineResolver.LooksSilent("/x {X} /qb", InstallerKind.Msi, runsMsiexec: true));
    }

    [Fact]
    public void Winget_portable_runs_the_user_alias()
    {
        InstalledApp app = App(InstallerKind.WingetPortable, @"winget uninstall --product-code ""Acme.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe""", environment: _environment);

        CommandResolution resolution = Resolver().Resolve(app, UninstallMode.Silent);

        Assert.Equal(Winget, resolution.Command?.ApplicationName);
        Assert.Equal(
            @"uninstall --product-code ""Acme.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe"" --silent --disable-interactivity",
            resolution.Command?.Arguments);
        Assert.Equal(CommandOrigin.Winget, resolution.Command?.Origin);
    }

    [Fact]
    public void A_package_is_removed_by_full_name_in_both_modes()
    {
        InstalledApp package = App(InstallerKind.Msix) with
        {
            Package = new PackageDetails("Acme.App_1.0.0.0_x64__abc", "Acme.App_abc", PackageSignatureKind.Store, null, null, false),
        };

        foreach (UninstallMode mode in Enum.GetValues<UninstallMode>())
        {
            CommandResolution resolution = Resolver().Resolve(package, mode);
            Assert.Null(resolution.Command);
            Assert.Equal("Acme.App_1.0.0.0_x64__abc", resolution.PackageFullName);
            Assert.Equal(ResolutionIssue.None, resolution.Issue);
            Assert.True(resolution.CanRun);
        }
    }

    [Fact]
    public void Missing_pieces_are_reported_without_a_command()
    {
        var bare = new FakeEnvironment();

        Assert.Equal(ResolutionIssue.NoCommand, Resolver().Resolve(App(InstallerKind.Msix), UninstallMode.Interactive).Issue);
        Assert.Equal(ResolutionIssue.NoCommand, Resolver().Resolve(App(InstallerKind.Nsis), UninstallMode.Interactive).Issue);

        CommandResolution missing = Resolver().Resolve(App(InstallerKind.Nsis, @"""C:\Gone\uninst.exe""", environment: _environment), UninstallMode.Interactive);
        Assert.Null(missing.Command);
        Assert.Equal(ResolutionIssue.ExecutableMissing, missing.Issue);

        InstalledApp msi = App(InstallerKind.Msi) with { MsiProductCode = ProductCode };
        Assert.Equal(ResolutionIssue.ExecutableMissing, Resolver(environment: bare).Resolve(msi, UninstallMode.Interactive).Issue);
        Assert.Equal(
            ResolutionIssue.ExecutableMissing,
            Resolver(environment: bare).Resolve(App(InstallerKind.WingetPortable, "winget uninstall --id Acme", environment: bare), UninstallMode.Interactive).Issue);
    }

    [Fact]
    public void Machine_entry_running_a_user_writable_binary_is_flagged()
    {
        var writable = new FakeLocations(LocationTrust.UserWritable);
        InstalledApp app = App(InstallerKind.Nsis, $@"""{Uninstaller}""", environment: _environment);

        CommandResolution machine = Resolver(writable).Resolve(app, UninstallMode.Interactive);
        CommandResolution user = Resolver(writable).Resolve(app with { Scope = InstallScope.User }, UninstallMode.Interactive);
        CommandResolution unknown = Resolver(new FakeLocations(LocationTrust.Unknown)).Resolve(app, UninstallMode.Interactive);

        CommandWarning warning = Assert.Single(machine.Warnings);
        Assert.Equal(CommandWarningKind.UserWritableLocation, warning.Kind);
        Assert.Contains(@"BUILTIN\Users", warning.Message, StringComparison.Ordinal);
        Assert.Empty(user.Warnings);
        Assert.Equal(CommandWarningKind.UnverifiedLocation, Assert.Single(unknown.Warnings).Kind);
    }

    [Fact]
    public void Synthesized_msiexec_is_not_checked_for_writable_locations()
    {
        var writable = new FakeLocations(LocationTrust.UserWritable);
        InstalledApp app = App(InstallerKind.Msi) with { MsiProductCode = ProductCode };

        CommandResolution resolution = Resolver(writable).Resolve(app, UninstallMode.Interactive);

        Assert.Empty(resolution.Warnings);
        Assert.Empty(writable.Checked);
    }

    [Fact]
    public void Shell_hosts_and_ambiguous_paths_are_warned_about()
    {
        var environment = new FakeEnvironment(@"C:\Windows\System32\cmd.exe", @"C:\Program Files\Acme\Tools\un.exe", @"C:\Program.exe");
        InstalledApp shell = App(InstallerKind.CustomExe, @"cmd.exe /c ""C:\Program Files\Acme\clean.bat""", environment: environment);
        InstalledApp ambiguous = App(InstallerKind.CustomExe, @"C:\Program Files\Acme\Tools\un.exe /remove", environment: environment);

        Assert.Equal(CommandWarningKind.ShellInterpreter, Assert.Single(Resolver(environment: environment).Resolve(shell, UninstallMode.Interactive).Warnings).Kind);

        CommandResolution resolution = Resolver(environment: environment).Resolve(ambiguous, UninstallMode.Interactive);
        Assert.Equal(@"C:\Program Files\Acme\Tools\un.exe", resolution.Command?.ApplicationName);
        Assert.Equal(CommandWarningKind.AmbiguousUnquotedPath, Assert.Single(resolution.Warnings).Kind);
    }

    [Fact]
    public void Acl_probe_flags_the_temp_folder_and_trusts_system32()
    {
        var probe = new AclWritableLocationProbe();
        string temp = Path.Combine(Path.GetTempPath(), $"niskala-acl-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(temp, []);
        try
        {
            Assert.Equal(LocationTrust.UserWritable, probe.Check(temp).Trust);
            Assert.Equal(LocationTrust.Trusted, probe.Check(Path.Combine(Environment.SystemDirectory, "msiexec.exe")).Trust);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private CommandLineResolver Resolver(IWritableLocationProbe? locations = null, ICommandEnvironment? environment = null) =>
        new(environment ?? _environment, locations ?? new FakeLocations());
}
