using Niskala.Core.Models;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Inventory;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class UninstallEntryMapperTests
{
    private static readonly UninstallEntryMapper Mapper = new(
        new InstallerKindDetector(new FakeProbe()),
        new FakeEnvironment(@"C:\Program Files\Acme\unins000.exe", @"C:\Windows\System32\MsiExec.exe"));

    [Fact]
    public void Maps_the_common_values()
    {
        var written = new DateTime(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);
        InstalledApp app = Mapper.Map(Entry(
            "Acme_is1",
            lastWrite: written,
            values:
            [
                ("DisplayName", "Acme Editor\0"),
                ("DisplayVersion", " 5.2.1 "),
                ("Publisher", "Acme Software"),
                ("InstallLocation", @"""C:\Program Files\Acme\"""),
                ("InstallDate", "20261112"),
                ("EstimatedSize", 2048),
                ("UninstallString", @"""C:\Program Files\Acme\unins000.exe"""),
                ("QuietUninstallString", @"""C:\Program Files\Acme\unins000.exe"" /SILENT"),
                ("NoModify", 1),
            ]));

        Assert.Equal("reg:hklm64:Acme_is1", app.Id);
        Assert.Equal(AppSources.Registry, app.Sources);
        Assert.Equal(written, app.KeyLastWriteUtc);
        Assert.Equal("Acme Editor", app.DisplayName);
        Assert.Equal("5.2.1", app.DisplayVersion);
        Assert.Equal(@"C:\Program Files\Acme", app.InstallLocation);
        Assert.Equal(InstallLocationOrigin.Registry, app.InstallLocationOrigin);
        Assert.Equal(new DateOnly(2026, 11, 12), app.InstallDate);
        Assert.Equal(InstallDateOrigin.Registry, app.InstallDateOrigin);
        Assert.Equal(2048L * 1024, app.EstimatedSizeBytes);
        Assert.Equal(InstallerKind.InnoSetup, app.InstallerKind);
        Assert.Equal("/SILENT", app.QuietUninstallCommand?.Arguments);
        Assert.True(app.NoModify);
        Assert.False(app.IsOrphan);
        Assert.False(app.IsUninstallerMissing);
        Assert.True(app.IsVisible);
    }

    [Theory]
    [InlineData(RegistryViewKind.Registry32, InstallScope.Machine, "reg:hklm32:K")]
    [InlineData(RegistryViewKind.Registry64, InstallScope.Machine, "reg:hklm64:K")]
    [InlineData(RegistryViewKind.Shared, InstallScope.User, "reg:hkcu:K")]
    public void Builds_stable_ids(RegistryViewKind view, InstallScope scope, string expected)
    {
        Assert.Equal(expected, UninstallEntryMapper.BuildId(Entry("K", scope, view)));
    }

    [Theory]
    [InlineData(null, false, null, null, HiddenReason.NoDisplayName)]
    [InlineData("Acme", true, null, null, HiddenReason.SystemComponent)]
    [InlineData("Acme Hotfix", false, "Acme", null, HiddenReason.ChildEntry)]
    [InlineData("KB123", false, null, "Security Update", HiddenReason.Update)]
    [InlineData("Acme SP1", false, null, "Service Pack", HiddenReason.Update)]
    [InlineData("Acme", false, null, "Application", HiddenReason.None)]
    public void Hidden_reasons(string? name, bool systemComponent, string? parent, string? releaseType, HiddenReason expected)
    {
        Assert.Equal(expected, UninstallEntryMapper.GetHiddenReason(name, systemComponent, parent, releaseType));
    }

    [Fact]
    public void DWORD_above_int_max_keeps_its_unsigned_value()
    {
        InstalledApp app = Mapper.Map(Entry("Big", values: [("DisplayName", "Big"), ("EstimatedSize", unchecked((int)0x8000_0000))]));
        Assert.Equal(0x8000_0000L * 1024, app.EstimatedSizeBytes);
    }

    [Fact]
    public void Bad_install_date_falls_back_to_key_write_time()
    {
        var written = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        InstalledApp app = Mapper.Map(Entry("Acme", lastWrite: written, values: [("DisplayName", "Acme"), ("InstallDate", "not a date")]));

        Assert.Equal(DateOnly.FromDateTime(written.ToLocalTime()), app.InstallDate);
        Assert.Equal(InstallDateOrigin.KeyLastWrite, app.InstallDateOrigin);
    }

    [Fact]
    public void Inno_app_path_fills_a_missing_install_location()
    {
        InstalledApp app = Mapper.Map(Entry("Acme_is1", values: [("DisplayName", "Acme"), ("Inno Setup: App Path", @"C:\Program Files\Acme")]));

        Assert.Equal(@"C:\Program Files\Acme", app.InstallLocation);
        Assert.Equal(InstallLocationOrigin.InnoAppPath, app.InstallLocationOrigin);
    }

    [Fact]
    public void Entry_without_any_command_is_an_orphan()
    {
        Assert.True(Mapper.Map(Entry("Acme", values: [("DisplayName", "Acme")])).IsOrphan);
    }

    [Fact]
    public void Msi_without_command_is_not_an_orphan()
    {
        InstalledApp app = Mapper.Map(Entry("{11111111-2222-3333-4444-555555555555}", values: [("DisplayName", "Acme"), ("WindowsInstaller", 1)]));

        Assert.False(app.IsOrphan);
        Assert.NotNull(app.MsiProductCode);
    }

    [Fact]
    public void Missing_uninstaller_file_is_flagged()
    {
        Assert.True(Mapper.Map(Entry("Acme", values: [("DisplayName", "Acme"), ("UninstallString", @"""C:\Gone\uninst.exe""")])).IsUninstallerMissing);
    }

    [Fact]
    public void Winget_portable_is_not_flagged_as_missing()
    {
        InstalledApp app = Mapper.Map(Entry(
            "Acme.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallScope.User,
            RegistryViewKind.Shared,
            values: [("DisplayName", "Acme Tool"), ("WinGetPackageIdentifier", "Acme.Tool"), ("WinGetInstallerType", "portable"), ("UninstallString", "winget uninstall --product-code Acme.Tool")]));

        Assert.Equal(InstallerKind.WingetPortable, app.InstallerKind);
        Assert.False(app.IsUninstallerMissing);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"""C:\Program Files\Acme\\""", @"C:\Program Files\Acme")]
    [InlineData("   ", null)]
    public void Cleans_registry_paths(string input, string? expected)
    {
        Assert.Equal(expected, Mapper.CleanPath(input));
    }
}
