using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Inventory;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class InstallerKindDetectorTests
{
    private const string ProductCode = "{11111111-2222-3333-4444-555555555555}";

    private static readonly FakeEnvironment Env = new(
        @"C:\Windows\System32\MsiExec.exe",
        @"C:\Program Files\Acme\uninst.exe",
        @"C:\Program Files\Acme\unins000.exe",
        @"C:\Users\bob\AppData\Local\acme\Update.exe",
        @"C:\Program Files\Acme\custom-remove.exe");

    private static InstallerDetection Detect(RawUninstallEntry entry, params string[] nsisFiles)
    {
        ParsedCommand? command = entry.GetString("UninstallString") is { } s ? CommandLineParser.Parse(s, Env) : null;
        return new InstallerKindDetector(new FakeProbe(nsisFiles)).Detect(entry, command);
    }

    [Fact]
    public void Msi_by_WindowsInstaller_flag_with_product_code_from_key_name()
    {
        InstallerDetection d = Detect(Entry(ProductCode, values: [("WindowsInstaller", 1), ("UninstallString", $"MsiExec.exe /I{ProductCode}")]));

        Assert.Equal(InstallerKind.Msi, d.Kind);
        Assert.Equal(Guid.Parse(ProductCode), d.MsiProductCode);
        Assert.Contains("WindowsInstaller=1", d.Evidence);
    }

    [Fact]
    public void Msi_by_msiexec_command_without_the_flag()
    {
        InstallerDetection d = Detect(Entry("AcmeMsi", values: [("UninstallString", $"MsiExec.exe /X{ProductCode} /qb")]));

        Assert.Equal(InstallerKind.Msi, d.Kind);
        Assert.Equal(Guid.Parse(ProductCode), d.MsiProductCode);
    }

    [Fact]
    public void InstallShield_authored_msi_stays_msi_with_evidence()
    {
        InstallerDetection d = Detect(Entry("InstallShield_" + ProductCode, values: [("WindowsInstaller", 1)]));

        Assert.Equal(InstallerKind.Msi, d.Kind);
        Assert.Contains("InstallShield-authored MSI", d.Evidence);
    }

    [Fact]
    public void Steam_by_its_key_name()
    {
        InstallerDetection d = Detect(Entry("Steam App 570", values: [("UninstallString", @"""C:\Program Files (x86)\Steam\steam.exe"" steam://uninstall/570")]));

        Assert.Equal(InstallerKind.Steam, d.Kind);
        Assert.Equal(["key name Steam App 570", "uninstalls through steam://uninstall"], d.Evidence);
    }

    [Fact]
    public void Inno_by_is1_key_and_values()
    {
        InstallerDetection d = Detect(Entry(
            "{A1B2C3D4-0000-0000-0000-000000000000}_is1",
            values: [("Inno Setup: App Path", @"C:\Program Files\Acme"), ("UninstallString", @"""C:\Program Files\Acme\unins000.exe""")]));

        Assert.Equal(InstallerKind.InnoSetup, d.Kind);
        Assert.Equal(2, d.Evidence.Count);
    }

    [Fact]
    public void Burn_bundle_key_guid_is_not_an_msi_product_code()
    {
        InstallerDetection d = Detect(Entry(
            "{99999999-0000-0000-0000-000000000000}",
            values: [("BundleUpgradeCode", new[] { "{X}" }), ("BundleProviderKey", "Acme.Bundle"), ("UninstallString", @"""C:\ProgramData\Package Cache\{9}\setup.exe"" /uninstall")]));

        Assert.Equal(InstallerKind.WixBurn, d.Kind);
        Assert.Null(d.MsiProductCode);
    }

    [Fact]
    public void InstallShield_script_setup()
    {
        InstallerDetection d = Detect(Entry(
            "AcmeIS",
            values: [("UninstallString", @"""C:\Program Files (x86)\InstallShield Installation Information\{X}\setup.exe"" -runfromtemp -l0x0409 -removeonly")]));

        Assert.Equal(InstallerKind.InstallShield, d.Kind);
    }

    [Fact]
    public void Squirrel_update_exe()
    {
        InstallerDetection d = Detect(Entry(
            "acme",
            InstallScope.User,
            RegistryViewKind.Shared,
            values: [("UninstallString", @"""C:\Users\bob\AppData\Local\acme\Update.exe"" --uninstall")]));

        Assert.Equal(InstallerKind.Squirrel, d.Kind);
    }

    [Fact]
    public void Winget_portable_package()
    {
        InstallerDetection d = Detect(Entry(
            "Acme.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallScope.User,
            RegistryViewKind.Shared,
            values: [("WinGetPackageIdentifier", "Acme.Tool"), ("WinGetInstallerType", "portable"), ("UninstallString", "winget uninstall --product-code Acme.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe")]));

        Assert.Equal(InstallerKind.WingetPortable, d.Kind);
    }

    [Fact]
    public void Winget_installed_inno_package_is_still_inno()
    {
        InstallerDetection d = Detect(Entry(
            "Acme_is1",
            values: [("WinGetPackageIdentifier", "Acme.Tool"), ("WinGetInstallerType", "inno"), ("UninstallString", @"""C:\Program Files\Acme\unins000.exe""")]));

        Assert.Equal(InstallerKind.InnoSetup, d.Kind);
        Assert.DoesNotContain(d.Evidence, e => e.StartsWith("WinGet", StringComparison.Ordinal));
    }

    [Fact]
    public void Nsis_by_file_probe()
    {
        InstallerDetection d = Detect(
            Entry("Acme", values: [("UninstallString", @"C:\Program Files\Acme\uninst.exe")]),
            nsisFiles: @"C:\Program Files\Acme\uninst.exe");

        Assert.Equal(InstallerKind.Nsis, d.Kind);
        Assert.Contains("NSIS uninstaller flag set", d.Evidence);
    }

    [Fact]
    public void Unknown_exe_is_custom()
    {
        Assert.Equal(InstallerKind.CustomExe, Detect(Entry("Acme", values: [("UninstallString", @"""C:\Program Files\Acme\custom-remove.exe"" --all")])).Kind);
    }

    [Fact]
    public void No_command_is_unknown()
    {
        Assert.Equal(InstallerKind.Unknown, Detect(Entry("Acme", values: [("DisplayName", "Acme")])).Kind);
    }
}
