using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Firewall;
using Niskala.Interop.Security;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class LeftoverScannerTests
{
    [Fact]
    public void Scores_folders_found_by_name_and_by_fingerprint()
    {
        using var scratch = new ScratchFolder();
        string programFiles = Dir(scratch, "ProgramFiles");
        string roaming = Dir(scratch, "Roaming");
        string local = Dir(scratch, "Local");
        string programData = Dir(scratch, "ProgramData");
        string startMenu = Dir(scratch, "StartMenu");

        string root = Path.GetDirectoryName(scratch.File(@"ProgramFiles\Acme Photo Editor\editor.exe", 10))!;
        string settings = Path.GetDirectoryName(scratch.File(@"Roaming\Acme\Photo Editor\settings.json", 20))!;
        scratch.File(@"Roaming\Acme\Studio\studio.json", 5);
        string cache = Path.GetDirectoryName(scratch.File(@"Local\AcmePhotoEditorCache\a.bin", 5))!;
        string signed = Path.GetDirectoryName(scratch.File(@"Local\PhotoEditor\tool.dll", 5))!;
        string guarded = Path.GetDirectoryName(scratch.File(@"ProgramData\Photo Editor\x.dat", 5))!;
        string studio = Path.GetDirectoryName(scratch.File(@"ProgramFiles\Acme Studio\studio.exe", 5))!;
        string link = scratch.File(@"StartMenu\Acme Photo Editor\Acme Photo Editor.lnk", 3);

        InstalledApp app = App("reg:hkcu:AcmePhotoEditor", "Acme Photo Editor") with { Publisher = "Acme Inc." };
        InstalledApp other = App("reg:hkcu:AcmeStudio", "Acme Studio") with
        {
            Publisher = "Acme Inc.",
            InstallLocation = studio,
            InstallLocationGuard = PathVerdict.Allowed,
        };
        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = DateTimeOffset.UtcNow,
            Roots = [root],
            Shortcuts = [new ShortcutTrace(link, Path.Combine(root, "editor.exe"), null, null)],
        };

        LeftoverScanner scanner = Scanner(
            [new(programFiles), new(roaming), new(local), new(programData), new(startMenu, Shortcuts: true)],
            new FakeTrust(signed),
            [new ProtectedLocation(guarded, ProtectionKind.Tree, "test")]);

        LeftoverReport report = scanner.Scan(app, fingerprint, [other], TestContext.Current.CancellationToken);

        LeftoverItem install = Item(report, root);
        Assert.Equal(ConfidenceTier.Safe, install.Tier);
        Assert.Equal(1, install.Files);
        Assert.Equal(10, install.Bytes);

        Assert.Equal(65, Item(report, settings).Score);
        Assert.Equal(0, Item(report, cache).Score);

        LeftoverItem menu = Item(report, Path.GetDirectoryName(link)!);
        Assert.Equal(ConfidenceTier.Safe, menu.Tier);
        Assert.Contains(menu.Reasons, r => r.Signal == LeftoverSignal.InFingerprint && r.Detail.StartsWith("holds Acme Photo Editor.lnk", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Items, i => i.Path == link);

        Assert.Contains(report.Excluded, e => e.Path == signed && e.Reason == ExclusionReason.MicrosoftSigned);
        Assert.Contains(report.Excluded, e => e.Path == guarded && e.Reason == ExclusionReason.Protected);
        Assert.DoesNotContain(report.Items, i => i.Path.Contains("Studio", StringComparison.Ordinal));
        Assert.Equal(report.Items.OrderByDescending(i => i.Score).Select(i => i.Score), report.Items.Select(i => i.Score));
    }

    [Fact]
    public void A_folder_that_holds_another_apps_install_folder_is_excluded()
    {
        using var scratch = new ScratchFolder();
        string programFiles = Dir(scratch, "ProgramFiles");
        string root = Path.GetDirectoryName(scratch.File(@"ProgramFiles\Acme Photo Editor\editor.exe", 10))!;
        string plugin = Path.GetDirectoryName(scratch.File(@"ProgramFiles\Acme Photo Editor\Plugins\plugin.exe", 10))!;

        InstalledApp app = App("reg:hkcu:AcmePhotoEditor", "Acme Photo Editor");
        InstalledApp other = App("reg:hkcu:Plugins", "Acme Photo Editor Plugins") with { InstallLocation = plugin, InstallLocationGuard = PathVerdict.Allowed };
        var fingerprint = new Fingerprint { AppId = app.Id, TakenAt = DateTimeOffset.UtcNow, Roots = [root] };

        LeftoverReport report = Scanner([new(programFiles)], new FakeTrust(), []).Scan(app, fingerprint, [other], TestContext.Current.CancellationToken);

        ExcludedLeftover excluded = Assert.Single(report.Excluded, e => e.Path == root);
        Assert.Equal(ExclusionReason.OtherAppFolder, excluded.Reason);
        Assert.DoesNotContain(report.Items, i => i.Path == root);
    }

    [Fact]
    public void Proposes_firewall_rules_only_when_every_rule_of_the_name_belongs_to_the_app()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Apps\Acme Photo Editor\editor.exe", 10))!;
        string elsewhere = Path.Combine(scratch.Root, @"Apps\Other\other.exe");
        InstalledApp app = App("reg:hkcu:AcmePhotoEditor", "Acme Photo Editor");
        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = DateTimeOffset.UtcNow,
            Roots = [root],
            Services = [new ServiceTrace("AcmeUpdate", null, Path.Combine(root, "update.exe"), null, 2)],
        };
        var firewall = new FakeFirewall(
        [
            Rule("Acme Photo Editor", Path.Combine(root, "editor.exe"), direction: 1),
            Rule("Acme Photo Editor", Path.Combine(root, "editor.exe"), direction: 2),
            Rule("Acme updater", null) with { ServiceName = "AcmeUpdate" },
            Rule("editor.exe", Path.Combine(root, "editor.exe")),
            Rule("editor.exe", elsewhere),
            Rule("Other", elsewhere),
        ]);

        LeftoverReport report = Scanner([], new FakeTrust(), [], firewall: firewall).Scan(app, fingerprint, [], TestContext.Current.CancellationToken);

        LeftoverItem named = Item(report, "Acme Photo Editor");
        Assert.Equal(LeftoverKind.FirewallRule, named.Kind);
        Assert.Equal(90, named.Score);
        Assert.Equal(60, Item(report, "Acme updater").Score);
        Assert.DoesNotContain(report.Items, i => i.Path is "editor.exe" or "Other");
        Assert.Contains(report.Notes, n => n.Contains("\"editor.exe\" is shared", StringComparison.Ordinal));
    }

    [Fact]
    public void Finds_keys_and_values_that_point_into_the_fingerprint()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Vendor\Photo Editor\editor.dll", 10))!;
        string id = Guid.NewGuid().ToString("N")[..8];
        string publisher = $"Qwzx{id}";
        string clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        string progId = $"Qwzx{id}.Editor";
        string vendorKey = $@"Software\{publisher}";
        try
        {
            using (RegistryKey product = Registry.CurrentUser.CreateSubKey($@"{vendorKey}\Photo Editor"))
            {
                product.SetValue("Path", root);
            }

            using (RegistryKey entry = Registry.CurrentUser.CreateSubKey($@"{vendorKey}\Entry"))
            {
                entry.SetValue("UninstallString", $@"""{root}\uninstall.exe""");
            }

            using (RegistryKey server = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\InprocServer32"))
            {
                server.SetValue(null, $@"{root}\editor.dll");
            }

            using (RegistryKey prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}\ProgID"))
            {
                prog.SetValue(null, progId);
            }

            using (RegistryKey back = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\CLSID"))
            {
                back.SetValue(null, clsid);
            }

            const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
            var run = new RegistryValueTrace(RunKey, "PhotoEditor", $@"""{root}\editor.exe"" --tray");
            var system = new FakeTraceSource
            {
                RunList = [run],
                ServiceList = [new ServiceTrace($"Qwzx{id}Svc", null, $@"{root}\service.exe", null, 3)],
            };

            InstalledApp app = App("reg:hkcu:Qwzx", $"{publisher} Photo Editor") with
            {
                Publisher = publisher,
                RegistryKey = $@"HKCU\{vendorKey}\Entry",
            };
            var fingerprint = new Fingerprint { AppId = app.Id, TakenAt = DateTimeOffset.UtcNow, Roots = [root], RunEntries = [run] };

            LeftoverReport report = Scanner([], new FakeTrust(), [], system).Scan(app, fingerprint, [], TestContext.Current.CancellationToken);

            Assert.Equal(65, Item(report, $@"HKCU\{vendorKey}\Photo Editor").Score);
            Assert.Equal(ConfidenceTier.Safe, Item(report, $@"HKCU\{vendorKey}\Entry").Tier);
            Assert.Equal(50, Item(report, $@"HKCU\Software\Classes\CLSID\{clsid}").Score);
            Assert.Equal(50, Item(report, $@"HKCU\Software\Classes\{progId}").Score);
            LeftoverItem value = Item(report, RunKey);
            Assert.Equal(("PhotoEditor", LeftoverKind.RegistryValue, 100), (value.ValueName, value.Kind, value.Score));
            Assert.Equal(50, Item(report, $@"HKLM\SYSTEM\CurrentControlSet\Services\Qwzx{id}Svc").Score);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(vendorKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Registrations_naming_the_install_folder_are_found_by_path()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Programs\Qwzx Viewer\viewer.exe", 10))!;
        string prefetch = Dir(scratch, "Prefetch");
        scratch.File(@"Prefetch\VIEWER.EXE-0A1B2C3D.pf", 4);
        scratch.File(@"Prefetch\OTHER.EXE-0A1B2C3D.pf", 4);
        string id = Guid.NewGuid().ToString("N")[..8];
        string progId = $"Qwzx{id}.Document";
        string protocol = $"qwzx{id}";
        string extension = $".qwzx{id}";
        string exe = $@"{root}\viewer.exe";
        const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string MuiCache = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        try
        {
            using (RegistryKey command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command"))
            {
                command.SetValue(null, $@"""{exe}"" ""%1""");
            }

            using (RegistryKey url = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}"))
            {
                url.SetValue("URL Protocol", string.Empty);
                using RegistryKey icon = url.CreateSubKey("DefaultIcon");
                icon.SetValue(null, $"{exe},0");
            }

            using (RegistryKey type = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}"))
            {
                type.SetValue(null, progId);
                using RegistryKey openWith = type.CreateSubKey("OpenWithProgids");
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                openWith.SetValue("Other.Program", Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (RegistryKey layers = Registry.CurrentUser.CreateSubKey(Layers))
            {
                layers.SetValue(exe, "~ HIGHDPIAWARE");
            }

            using (RegistryKey mui = Registry.CurrentUser.CreateSubKey(MuiCache))
            {
                mui.SetValue($"{exe}.FriendlyAppName", "Qwzx Viewer");
            }

            InstalledApp app = App("reg:hkcu:QwzxViewer", "Qwzx Viewer");
            var fingerprint = new Fingerprint { AppId = app.Id, TakenAt = DateTimeOffset.UtcNow, Roots = [root], ExecutableNames = ["viewer.exe"] };
            var system = new FakeTraceSource();
            var scanner = new LeftoverScanner(
                ZGuard(),
                RegistryGuard.ForCurrentMachine(),
                new FakeTrust(),
                new FileLeftoverProbe([], system, prefetch),
                new RegistryLeftoverProbe(system, NullLogger<RegistryLeftoverProbe>.Instance),
                new FirewallLeftoverProbe(new FakeFirewall([])),
                new SteamLeftoverProbe(() => null),
                new ScheduledTaskLeftoverProbe(system),
                new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
                SystemCommandEnvironment.Instance,
                NullLogger<LeftoverScanner>.Instance);

            LeftoverReport report = scanner.Scan(app, fingerprint, [], TestContext.Current.CancellationToken);

            Assert.Contains("ProgID", Item(report, $@"HKCU\Software\Classes\{progId}").Reasons[0].Detail, StringComparison.Ordinal);
            Assert.Contains("URL protocol", Item(report, $@"HKCU\Software\Classes\{protocol}").Reasons[0].Detail, StringComparison.Ordinal);
            Assert.Equal(string.Empty, Item(report, $@"HKCU\Software\Classes\{extension}").ValueName);
            Assert.Equal(progId, Item(report, $@"HKCU\Software\Classes\{extension}\OpenWithProgids").ValueName);
            Assert.DoesNotContain(report.Items, i => i.Kind == LeftoverKind.RegistryKey && i.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            Assert.Single(report.Items, i => i.Path == $@"HKCU\{Layers}" && i.ValueName == exe);
            Assert.Single(report.Items, i => i.Path == $@"HKCU\{MuiCache}" && i.ValueName == $"{exe}.FriendlyAppName");
            Assert.Equal(ConfidenceTier.Review, Item(report, Path.Combine(prefetch, "VIEWER.EXE-0A1B2C3D.pf")).Tier);
            Assert.DoesNotContain(report.Items, i => i.Path.EndsWith("OTHER.EXE-0A1B2C3D.pf", StringComparison.Ordinal));
        }
        finally
        {
            foreach (string key in (string[])[$@"Software\Classes\{progId}", $@"Software\Classes\{protocol}", $@"Software\Classes\{extension}"])
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }

            using (RegistryKey? layers = Registry.CurrentUser.OpenSubKey(Layers, writable: true))
            {
                layers?.DeleteValue(exe, throwOnMissingValue: false);
            }

            using RegistryKey? mui = Registry.CurrentUser.OpenSubKey(MuiCache, writable: true);
            mui?.DeleteValue($"{exe}.FriendlyAppName", throwOnMissingValue: false);
        }
    }

    [Fact]
    public void Nested_candidates_fold_into_their_folder()
    {
        LeftoverCandidate folder = new(LeftoverKind.Folder, @"C:\Data\Acme", null, "Acme", null, [(LeftoverSignal.ExactName, "name")]);
        LeftoverCandidate file = new(LeftoverKind.File, @"C:\Data\Acme\Acme.lnk", null, "Acme", null, [(LeftoverSignal.InFingerprint, "shortcut"), (LeftoverSignal.ExactName, "name")]);
        LeftoverCandidate sibling = new(LeftoverKind.Folder, @"C:\Data\AcmeTools", null, "AcmeTools", null, [(LeftoverSignal.PartialName, "partial")]);

        List<LeftoverCandidate> collapsed = [.. LeftoverScanner.Collapse([folder, file, sibling])];

        Assert.Equal([folder.Path, sibling.Path], collapsed.Select(c => c.Path));
        Assert.Equal([LeftoverSignal.ExactName, LeftoverSignal.InFingerprint], collapsed[0].Evidence.Select(e => e.Signal));
        Assert.Equal("holds Acme.lnk: shortcut", collapsed[0].Evidence[1].Detail);
    }

    [Fact]
    public void Recognizes_a_Microsoft_signature()
    {
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe");
        Assert.SkipUnless(File.Exists(dotnet), "The .NET host is not installed in Program Files.");

        Assert.Equal(SignatureCheck.MicrosoftSigned, WinTrustNative.CheckFile(dotnet));
        Assert.Equal(SignatureCheck.NotTrusted, WinTrustNative.CheckFile(typeof(LeftoverScannerTests).Assembly.Location));
    }

    [Fact]
    public void Stores_and_reloads_fingerprints()
    {
        using var scratch = new ScratchFolder();
        var store = new FingerprintStore(Path.Combine(scratch.Root, "fingerprints"), NullLogger<FingerprintStore>.Instance);
        InstalledApp app = App("msi:{5B2C2A1E-8E54-4C11-9F2F-3D63E4B0C1A7}", "Acme") with { MsiProductCode = Guid.NewGuid(), InstallerKind = InstallerKind.Msi };
        var first = new Fingerprint { AppId = app.Id, TakenAt = DateTimeOffset.UtcNow, Roots = [@"C:\Acme"] };
        Fingerprint second = first with { Roots = [@"C:\Acme 2"], ExecutableNames = ["acme.exe"] };

        Assert.NotNull(store.Save(app, first));
        Assert.NotNull(store.Save(app, second));

        FingerprintRecord record = Assert.Single(store.LoadAll());
        Assert.Equal(app.MsiProductCode, record.App.MsiProductCode);
        Assert.Equal(InstallerKind.Msi, record.App.InstallerKind);
        Assert.Equal([@"C:\Acme 2"], record.Fingerprint.Roots);
        Assert.Equal(["acme.exe"], record.Fingerprint.ExecutableNames);
        Assert.Empty(Directory.GetFiles(Path.Combine(scratch.Root, "fingerprints"), "*.tmp"));
    }

    private static LeftoverScanner Scanner(
        IReadOnlyList<FileSearchRoot> roots,
        IFileTrustProbe trust,
        ProtectedLocation[] rules,
        FakeTraceSource? system = null,
        IFirewallRuleSource? firewall = null,
        SteamLeftoverProbe? steam = null)
    {
        system ??= new FakeTraceSource();
        return new LeftoverScanner(
            ZGuard(rules),
            RegistryGuard.ForCurrentMachine(),
            trust,
            new FileLeftoverProbe(roots, system),
            new RegistryLeftoverProbe(system, NullLogger<RegistryLeftoverProbe>.Instance),
            new FirewallLeftoverProbe(firewall ?? new FakeFirewall([])),
            steam ?? new SteamLeftoverProbe(() => null),
            new ScheduledTaskLeftoverProbe(system),
            new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
            SystemCommandEnvironment.Instance,
            NullLogger<LeftoverScanner>.Instance);
    }

    private static string Dir(ScratchFolder scratch, string name) => Directory.CreateDirectory(Path.Combine(scratch.Root, name)).FullName;

    private static LeftoverItem Item(LeftoverReport report, string path) =>
        Assert.Single(report.Items, i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static FirewallRule Rule(string name, string? application, int direction = 1) =>
        new(name, null, application, null, 6, null, null, null, null, null, direction, "All", true, null, 7, false, 1);

    private sealed class FakeFirewall(List<FirewallRule> rules) : IFirewallRuleSource
    {
        public int TryList(out List<FirewallRule> list)
        {
            list = rules;
            return 0;
        }
    }

    private sealed class FakeTrust(params string[] microsoftPaths) : IFileTrustProbe
    {
        public ExclusionReason? Check(string path, bool isDirectory) =>
            microsoftPaths.Contains(path, StringComparer.OrdinalIgnoreCase) ? ExclusionReason.MicrosoftSigned : null;
    }
}
