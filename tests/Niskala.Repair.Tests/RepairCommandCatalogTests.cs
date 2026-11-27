using Niskala.Repair;

namespace Niskala.Repair.Tests;

public sealed class RepairCommandCatalogTests
{
    private static readonly string System32 = Environment.SystemDirectory;

    [Fact]
    public void Catalog_order_matches_the_closed_enum()
    {
        RepairCommandId[] ids = Enum.GetValues<RepairCommandId>();
        Assert.Equal(ids.Length, RepairCommandCatalog.All.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], RepairCommandCatalog.All[i].Id);
            Assert.Same(RepairCommandCatalog.All[i], RepairCommandCatalog.Get(ids[i]));
        }

        Assert.Equal(ids.Length, RepairCommandCatalog.All.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_undefined_id_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairCommandCatalog.Get((RepairCommandId)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairCommandCatalog.Resolve((RepairCommandId)(-1), new FakeRepairEnvironment(System32)));
    }

    [Fact]
    public void Every_command_runs_a_program_straight_from_System32()
    {
        foreach (RepairCommand command in RepairCommandCatalog.All)
        {
            RepairInvocation invocation = RepairCommandCatalog.Resolve(command.Id, SystemRepairEnvironment.Instance);
            Assert.Equal(System32, Path.GetDirectoryName(invocation.FileName), StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(invocation.FileName), invocation.FileName);
            Assert.Equal(0, (int)(File.GetAttributes(invocation.FileName) & FileAttributes.ReparsePoint));
        }
    }

    [Theory]
    [InlineData(RepairCommandId.DismCheckHealth, "Dism.exe /Online /Cleanup-Image /CheckHealth")]
    [InlineData(RepairCommandId.DismRestoreHealth, "Dism.exe /Online /Cleanup-Image /RestoreHealth")]
    [InlineData(RepairCommandId.SfcScanNow, "sfc.exe /scannow")]
    [InlineData(RepairCommandId.DismComponentCleanup, "Dism.exe /Online /Cleanup-Image /StartComponentCleanup")]
    [InlineData(RepairCommandId.NetshWinsockReset, "netsh.exe winsock reset")]
    [InlineData(RepairCommandId.NetshIpReset, "netsh.exe int ip reset")]
    [InlineData(RepairCommandId.FlushDns, "ipconfig.exe /flushdns")]
    [InlineData(RepairCommandId.ChkdskOnlineScan, "chkdsk.exe D: /scan")]
    public void Each_command_has_exactly_its_documented_arguments(RepairCommandId id, string expected)
    {
        RepairInvocation invocation = RepairCommandCatalog.Resolve(id, new FakeRepairEnvironment(@"D:\Windows\System32"));
        Assert.Equal(expected, invocation.CommandLine);
        Assert.Equal(@"D:\Windows\System32\" + invocation.Executable, invocation.FileName);
    }

    [Fact]
    public void The_online_disk_scan_targets_the_live_system_drive()
    {
        RepairInvocation invocation = RepairCommandCatalog.Resolve(RepairCommandId.ChkdskOnlineScan, SystemRepairEnvironment.Instance);
        Assert.Equal(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), invocation.Arguments[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("/scan", invocation.Arguments[1]);
        Assert.Equal(2, invocation.Arguments.Count);
    }

    [Theory]
    [InlineData(@"\\server\share\Windows\System32")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Windows\System32")]
    public void A_system_directory_without_a_drive_letter_is_refused(string systemDirectory)
    {
        Assert.Throws<InvalidOperationException>(() => RepairCommandCatalog.Resolve(RepairCommandId.ChkdskOnlineScan, new FakeRepairEnvironment(systemDirectory)));
    }

    [Fact]
    public void No_catalog_command_carries_a_destructive_switch()
    {
        string[] forbidden = ["/ResetBase", "/f", "/r", "/x", "/b", "/spotfix", "/offlinescanandfix"];
        foreach (RepairCommand command in RepairCommandCatalog.All)
        {
            RepairInvocation invocation = RepairCommandCatalog.Resolve(command.Id, SystemRepairEnvironment.Instance);
            Assert.DoesNotContain(invocation.Arguments, a => forbidden.Contains(a, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(invocation.Arguments, RepairInvocation.IsDestructive);
        }
    }

    [Fact]
    public void Component_cleanup_keeps_the_update_uninstall_points()
    {
        RepairInvocation invocation = RepairCommandCatalog.Resolve(RepairCommandId.DismComponentCleanup, SystemRepairEnvironment.Instance);
        Assert.Contains("/StartComponentCleanup", invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Dism.exe", "/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase")]
    [InlineData("Dism.exe", "/Online", "/Cleanup-Image", "/RevertPendingActions")]
    [InlineData("chkdsk.exe", "C:", "/f")]
    [InlineData("chkdsk.exe", "C:", "/r")]
    [InlineData("chkdsk.exe", "C:", "/X")]
    [InlineData("chkdsk.exe", "C:", "/spotfix")]
    [InlineData("chkdsk.exe", "C:", "/offlinescanandfix")]
    [InlineData("Dism.exe", "/Online", "/Cleanup-Image", "/RestoreHealth", "/Source:C:\\Windows\\WinSxS")]
    [InlineData("Dism.exe", "/Online", "/Cleanup-Image", "/RestoreHealth", "/Source:wim:\\\\server\\share\\x.wim:1")]
    [InlineData("Dism.exe", "/Online", "/Cleanup-Image", "/RestoreHealth", "/Source:wim:D:\\x.esd:1")]
    [InlineData("netsh.exe", "advfirewall", "reset")]
    [InlineData("sfc.exe", "/scannow", "&", "format")]
    public void An_invocation_refuses_arguments_outside_the_allowlist(string executable, params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => new RepairInvocation(System32, executable, arguments));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData(@"..\Dism.exe")]
    [InlineData("format.com")]
    public void An_invocation_refuses_programs_outside_the_allowlist(string executable)
    {
        Assert.Throws<ArgumentException>(() => new RepairInvocation(System32, executable, ["/scannow"]));
    }

    [Fact]
    public void An_invocation_needs_a_full_system_directory()
    {
        Assert.Throws<ArgumentException>(() => new RepairInvocation(@"Windows\System32", "sfc.exe", ["/scannow"]));
    }

    [Theory]
    [InlineData("dism-check-health", RepairCommandId.DismCheckHealth)]
    [InlineData(" SFC-SCAN ", RepairCommandId.SfcScanNow)]
    [InlineData("chkdsk-scan", RepairCommandId.ChkdskOnlineScan)]
    public void Commands_are_found_by_name(string name, RepairCommandId expected)
    {
        Assert.True(RepairCommandCatalog.TryFind(name, out RepairCommand? command));
        Assert.Equal(expected, command.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("format")]
    [InlineData("DismCheckHealth")]
    [InlineData("dism /online /cleanup-image /resetbase")]
    public void Unknown_names_find_nothing(string name)
    {
        Assert.False(RepairCommandCatalog.TryFind(name, out _));
    }

    [Fact]
    public void Every_command_needs_elevation_and_only_the_network_resets_need_a_restart()
    {
        Assert.All(RepairCommandCatalog.All, c => Assert.True(c.NeedsElevation, c.Name));
        Assert.Equal(
            [RepairCommandId.NetshWinsockReset, RepairCommandId.NetshIpReset],
            RepairCommandCatalog.All.Where(c => c.NeedsRestart).Select(c => c.Id));
    }
}

internal sealed class FakeRepairEnvironment(string systemDirectory, bool elevated = true, int oemCodePage = 437) : IRepairEnvironment
{
    public bool IsElevated => elevated;

    public string SystemDirectory => systemDirectory;

    public int OemCodePage => oemCodePage;
}
