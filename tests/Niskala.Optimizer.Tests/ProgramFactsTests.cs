using Niskala.Core.Safety;
using Niskala.Interop.Security;
using Niskala.Optimizer.Resources;
using Niskala.Scanner.Sizing;

namespace Niskala.Optimizer.Tests;

public sealed class ProgramFactsTests
{
    [Fact]
    public void Microsoft_binaries_under_the_2010_product_root_count_as_Microsoft()
    {
        string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
        string? signed = Directory.Exists(platform)
            ? Directory.EnumerateFiles(platform, "MpDefenderCoreService.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (signed is null)
        {
            Assert.Skip("No Defender platform binary on this machine.");
        }

        Assert.Equal(SignatureCheck.MicrosoftSigned, WinTrustNative.CheckFile(signed));
    }

    [Fact]
    public void Microsoft_binaries_under_the_2011_application_root_count_as_Microsoft()
    {
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        if (!File.Exists(edge))
        {
            Assert.Skip("Edge is not installed in its usual folder.");
        }

        Assert.Equal(SignatureCheck.MicrosoftSigned, WinTrustNative.CheckFile(edge));
    }

    [Fact]
    public void Programs_inside_protected_Windows_component_folders_count_as_Microsoft()
    {
        using var sandbox = new Sandbox();
        string program = sandbox.File(@"Component\tool.exe", 128);
        var guard = new PathGuard(
            ProtectedLocations.Create([new ProtectedLocation(Path.GetDirectoryName(program)!, ProtectionKind.Tree, "test component")], Win32PathResolver.Instance),
            Win32PathResolver.Instance);
        var free = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);

        Assert.True(new SystemProgramFacts(new DiskKindProbe(), guard).Describe(program).IsMicrosoft);
        Assert.False(new SystemProgramFacts(new DiskKindProbe(), free).Describe(program).IsMicrosoft);
    }
}
