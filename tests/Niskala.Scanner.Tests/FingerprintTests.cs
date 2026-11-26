using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Inventory.Msi;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class FingerprintTests
{
    private static readonly Guid ProductCode = Guid.Parse("{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}");
    private static readonly Guid UpgradeCode = Guid.Parse("{0E1D2C3B-4A59-6877-8695-A4B3C2D1E0F9}");

    [Fact]
    public void Records_the_install_folder_and_every_trace_that_points_into_it()
    {
        using var scratch = new ScratchFolder();
        string root = Path.Combine(scratch.Root, "Acme");
        string sibling = Path.Combine(scratch.Root, "Acme Tools");
        scratch.File(@"Acme\acme.exe", 100);
        scratch.File(@"Acme\bin\helper.EXE", 50);
        scratch.File(@"Acme\bin\core.dll", 25);
        scratch.File(@"Acme Tools\tool.exe", 10);

        var system = new FakeTraceSource
        {
            ServiceList =
            [
                new ServiceTrace("AcmeSvc", "Acme Service", $@"{root}\bin\helper.exe", null, 2),
                new ServiceTrace("AcmeHost", null, @"C:\Windows\System32\svchost.exe", $@"{root}\bin\core.dll", 3),
                new ServiceTrace("ToolSvc", null, $@"{sibling}\tool.exe", null, 2),
            ],
            RunList =
            [
                new RegistryValueTrace(@"HKCU\Run", "Acme", $@"""{root}\acme.exe"" --tray"),
                new RegistryValueTrace(@"HKCU\Run", "AcmeDll", $@"rundll32.exe ""{root}\bin\core.dll"",Start"),
                new RegistryValueTrace(@"HKCU\Run", "Tool", $@"""{sibling}\tool.exe"""),
            ],
            AppPathList =
            [
                new RegistryValueTrace(@"HKLM\App Paths\acme.exe", null, $@"{root}\acme.exe"),
                new RegistryValueTrace(@"HKLM\App Paths\acme.exe", "Path", $@"{root}\bin;C:\Shared"),
                new RegistryValueTrace(@"HKLM\App Paths\tool.exe", null, $@"{sibling}\tool.exe"),
            ],
            TaskList = new TaskList(
                [
                    new TaskTrace(@"\Acme Update", $@"{root}\acme.exe", "/update"),
                    new TaskTrace(@"\Acme Cleanup", @"C:\Windows\System32\cmd.exe", $@"/c del ""{root}\cache"""),
                    new TaskTrace(@"\Tool", $@"{sibling}\tool.exe", null),
                ],
                UnreadableItems: 2,
                Error: 0),
            ShortcutList =
            [
                new ShortcutTrace(@"C:\Start\Acme.lnk", $@"{root}\acme.exe", null, null),
                new ShortcutTrace(@"C:\Start\Acme Docs.lnk", @"C:\Windows\explorer.exe", $@"""{root}\docs""", null),
                new ShortcutTrace(@"C:\Start\Acme Advertised.lnk", null, null, ProductCode),
                new ShortcutTrace(@"C:\Start\Tool.lnk", $@"{sibling}\tool.exe", null, null),
            ],
            UpgradeCodes = { [ProductCode] = UpgradeCode },
        };

        InstalledApp app = App("reg:hklm64:{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}") with
        {
            InstallLocation = root,
            InstallerKind = InstallerKind.Msi,
            MsiProductCode = ProductCode,
        };

        Fingerprint fingerprint = Collector(system).Collect(app, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([root], fingerprint.Roots);
        Assert.Equal(3, fingerprint.Files!.Files);
        Assert.Equal(1, fingerprint.Files.Directories);
        Assert.Equal(175, fingerprint.Files.Bytes);
        Assert.Contains(fingerprint.Files.Entries, e => e.RelativePath == @"bin\core.dll" && e.Size == 25);
        Assert.Equal(["acme.exe", "helper.EXE"], fingerprint.ExecutableNames);
        Assert.Equal(["AcmeSvc", "AcmeHost"], fingerprint.Services.Select(s => s.Name));
        Assert.Equal(["Acme", "AcmeDll"], fingerprint.RunEntries.Select(r => r.ValueName));
        Assert.Equal(2, fingerprint.AppPaths.Count);
        Assert.Equal([@"\Acme Update", @"\Acme Cleanup"], fingerprint.ScheduledTasks.Select(t => t.Path));
        Assert.Equal(3, fingerprint.Shortcuts.Count);
        Assert.DoesNotContain(fingerprint.Shortcuts, s => s.LinkPath.EndsWith("Tool.lnk", StringComparison.Ordinal));
        Assert.Equal(ProductCode, fingerprint.MsiProductCode);
        Assert.Equal(UpgradeCode, fingerprint.MsiUpgradeCode);
        Assert.Contains(fingerprint.Notes, note => note.Contains("2 scheduled task", StringComparison.Ordinal));
    }

    [Fact]
    public void Junctions_inside_the_install_folder_are_recorded_but_not_walked()
    {
        using var scratch = new ScratchFolder();
        scratch.File(@"Acme\acme.exe", 10);
        scratch.File(@"Elsewhere\big.bin", 1000);
        scratch.Junction(@"Acme\data", Path.Combine(scratch.Root, "Elsewhere"));
        InstalledApp app = App("a") with { InstallLocation = Path.Combine(scratch.Root, "Acme") };

        Fingerprint fingerprint = Collector(new FakeTraceSource()).Collect(app, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fingerprint.Files!.Files);
        Assert.Equal(10, fingerprint.Files.Bytes);
        FileTreeEntry link = Assert.Single(fingerprint.Files.Entries, e => e.IsDirectory);
        Assert.True(link.IsReparsePoint);
        Assert.DoesNotContain(fingerprint.Files.Entries, e => e.RelativePath.EndsWith("big.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_denied_install_folder_owns_nothing_but_product_code_still_matches()
    {
        var system = new FakeTraceSource
        {
            ServiceList = [new ServiceTrace("Other", null, @"Z:\Program Files\Other\svc.exe", null, 2)],
            ShortcutList =
            [
                new ShortcutTrace(@"C:\Start\Advertised.lnk", null, null, ProductCode),
                new ShortcutTrace(@"C:\Start\Other.lnk", @"Z:\Program Files\Other\other.exe", null, null),
            ],
        };
        InstalledApp app = App("m") with { InstallLocation = @"Z:\Program Files", MsiProductCode = ProductCode };

        Fingerprint fingerprint = Collector(system).Collect(app, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(fingerprint.Roots);
        Assert.Null(fingerprint.Files);
        Assert.Empty(fingerprint.Services);
        Assert.Equal(@"C:\Start\Advertised.lnk", Assert.Single(fingerprint.Shortcuts).LinkPath);
        Assert.Contains(fingerprint.Notes, note => note.Contains(nameof(PathVerdict.ProtectedSelf), StringComparison.Ordinal));
        Assert.Equal(0, system.ServiceReads);
    }

    [Fact]
    public void File_list_is_capped_but_totals_count_everything()
    {
        using var scratch = new ScratchFolder();
        for (int i = 0; i < 5; i++)
        {
            scratch.File($@"Acme\f{i}.txt", 1);
        }

        InstalledApp app = App("a") with { InstallLocation = Path.Combine(scratch.Root, "Acme") };

        Fingerprint fingerprint = Collector(new FakeTraceSource()).Collect(app, new FingerprintOptions { MaxFileEntries = 2 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fingerprint.Files!.Entries.Count);
        Assert.Equal(5, fingerprint.Files.Files);
        Assert.True(fingerprint.Files.Truncated);
    }

    [Theory]
    [InlineData("Acme_is1", InstallerKind.InnoSetup, "Acme")]
    [InlineData("{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}_is1", InstallerKind.InnoSetup, "{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}")]
    [InlineData("Acme_is1", InstallerKind.Nsis, null)]
    [InlineData("_is1", InstallerKind.InnoSetup, null)]
    public void Inno_app_id_comes_from_the_key_name(string keyName, InstallerKind kind, string? expected)
    {
        Assert.Equal(expected, FingerprintCollector.InnoAppId(App("a") with { KeyName = keyName, InstallerKind = kind }));
    }

    [Fact]
    public void Task_xml_yields_exec_actions_only()
    {
        const string Xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>"C:\Program Files\Acme\acme.exe"</Command>
                  <Arguments> /update </Arguments>
                </Exec>
                <ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler>
                <Exec><Arguments>/x</Arguments><Command>%SystemRoot%\System32\cmd.exe</Command></Exec>
              </Actions>
            </Task>
            """;

        IReadOnlyList<TaskTrace> actions = TaskXml.ReadExecActions(@"\Acme", Xml);

        Assert.Equal(
            [
                new TaskTrace(@"\Acme", @"""C:\Program Files\Acme\acme.exe""", "/update"),
                new TaskTrace(@"\Acme", @"%SystemRoot%\System32\cmd.exe", "/x"),
            ],
            actions);
        Assert.Empty(TaskXml.ReadExecActions(@"\Broken", "<Task><Actions>"));
    }

    [Fact]
    public void Packed_guid_unpacks_to_the_original()
    {
        string packed = MsiPackedGuid.Pack(ProductCode);

        Assert.True(MsiPackedGuid.TryUnpack(packed, out Guid unpacked));
        Assert.Equal(ProductCode, unpacked);
        Assert.False(MsiPackedGuid.TryUnpack("1234", out _));
    }

    [Fact]
    public void System_sources_read_this_machine()
    {
        var source = new SystemTraceSource(SystemCommandEnvironment.Instance, NullLogger<SystemTraceSource>.Instance);

        Assert.Contains(source.Services(), s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        TaskList tasks = source.ScheduledTasks();
        Assert.Equal(0, tasks.Error);
        Assert.NotEmpty(tasks.Tasks);
        Assert.NotEmpty(source.Shortcuts());
    }

    [Fact]
    public void Shared_redistributables_are_not_named_as_the_game_s_programs()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Game\game.exe", 10))!;
        scratch.File(@"Game\UnityCrashHandler64.exe", 10);
        scratch.File(@"Game\_CommonRedist\vcredist\2019\VC_redist.x64.exe", 10);
        scratch.File(@"Game\_CommonRedist\DirectX\Jun2010\DXSETUP.exe", 10);
        scratch.File(@"Game\Engine\Binaries\CrashReportClient.exe", 10);

        Fingerprint fingerprint = Collector(new FakeTraceSource()).Collect(App("reg:hklm64:Steam App 4000") with { InstallLocation = root }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["game.exe"], fingerprint.ExecutableNames);
        Assert.Equal(5, fingerprint.Files!.Files);
    }

    private static FingerprintCollector Collector(ISystemTraceSource system) => new(
        ZGuard(new ProtectedLocation(@"Z:\Program Files", ProtectionKind.Self, "pf")),
        system,
        SystemCommandEnvironment.Instance,
        NullLogger<FingerprintCollector>.Instance);
}
