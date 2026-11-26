using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Cleaner.Registry;
using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Scanner.Leftovers;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Cleaner.Tests;

public sealed class RegistryIntegrityTests : IDisposable
{
    private readonly ScratchFolder _scratch = new();
    private readonly string _key = $@"{TestRoots.Key}\{Guid.NewGuid():N}";
    private readonly string _gone;
    private readonly string _present;
    private readonly FakeFirewall _firewall;
    private readonly IntegrityLocations _locations;

    public RegistryIntegrityTests()
    {
        _gone = Path.Combine(_scratch.Root, "gone");
        _present = _scratch.File(@"present\tool.exe");
        _firewall = new FakeFirewall(
            FakeFirewall.Rule("Nsk Gone", Path.Combine(_gone, "net.exe")),
            FakeFirewall.Rule("Nsk Mixed", Path.Combine(_gone, "net.exe")),
            FakeFirewall.Rule("Nsk Mixed", _present));
        _locations = new IntegrityLocations([Hkcu("Classes")], [Hkcu("SharedDLLs")], Hkcu("Services"));

        Set(@"Classes\.nskgone", string.Empty, "Nsk.MissingType");
        Set(@"Classes\.nskok", string.Empty, "Nsk.Present");
        Set(@"Classes\Nsk.Present", string.Empty, "Present type");
        Set(@"Classes\Nsk.Broken\shell\open\command", string.Empty, $"\"{Path.Combine(_gone, "app.exe")}\" \"%1\"");
        Set(@"Classes\Nsk.Fine\shell\open\command", string.Empty, $"\"{_present}\" \"%1\"");
        Set(@"Classes\Nsk.Windows\shell\open\command", string.Empty, @"%SystemRoot%\system32\nskala-missing.exe %1");
        Set(@"Classes\CLSID\{6F1C2B9E-0D7A-4C1F-9E57-2A1B3C4D5E61}\InprocServer32", string.Empty, Path.Combine(_gone, "shell.dll"));
        Set(@"Classes\CLSID\{6F1C2B9E-0D7A-4C1F-9E57-2A1B3C4D5E62}\InprocServer32", string.Empty, Path.Combine(_gone, "proxy.dll"));
        Set(@"Classes\CLSID\{6F1C2B9E-0D7A-4C1F-9E57-2A1B3C4D5E62}\LocalServer32", string.Empty, $"\"{_present}\" /automation");
        Set("SharedDLLs", Path.Combine(_gone, "shared.dll"), 1);
        Set("SharedDLLs", _present, 1);
        Set(@"Services\NskGone", "Type", 16);
        Set(@"Services\NskGone", "ImagePath", $"\"{Path.Combine(_gone, "svc.exe")}\" -run");
        Set(@"Services\NskDriver", "Type", 1);
        Set(@"Services\NskDriver", "ImagePath", Path.Combine(_gone, "drv.sys"));
    }

    [Fact]
    public void Every_check_reports_only_what_points_at_missing_programs()
    {
        IntegrityReport report = Scanner().Scan(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                (IntegrityIssueKind.FileAssociation, LeftoverKind.RegistryValue, Hkcu(@"Classes\.nskgone")),
                (IntegrityIssueKind.FileAssociation, LeftoverKind.RegistryKey, Hkcu(@"Classes\Nsk.Broken\shell\open")),
                (IntegrityIssueKind.SharedDll, LeftoverKind.RegistryValue, Hkcu("SharedDLLs")),
                (IntegrityIssueKind.ComRegistration, LeftoverKind.RegistryKey, Hkcu(@"Classes\CLSID\{6F1C2B9E-0D7A-4C1F-9E57-2A1B3C4D5E61}")),
                (IntegrityIssueKind.FirewallRule, LeftoverKind.FirewallRule, "Nsk Gone"),
                (IntegrityIssueKind.ServicePath, LeftoverKind.RegistryKey, Hkcu(@"Services\NskGone")),
            ],
            report.Issues.Select(i => (i.Kind, i.ItemKind, i.Path)));
        Assert.All(report.Issues, i => Assert.True(i.Fixable, i.Protection));
    }

    [Fact]
    public void Only_the_chosen_checks_run()
    {
        IntegrityReport report = Scanner().Scan([IntegrityIssueKind.SharedDll], TestContext.Current.CancellationToken);

        IntegrityIssue issue = Assert.Single(report.Issues);
        Assert.Equal(Path.Combine(_gone, "shared.dll"), issue.ValueName);
    }

    [Fact]
    public async Task Fixing_backs_up_to_a_reg_file_first_and_restore_puts_everything_back()
    {
        RegistryIntegrityScanner scanner = Scanner();
        IntegrityReport report = scanner.Scan(cancellationToken: TestContext.Current.CancellationToken);
        TransactionStore store = Fixtures.Store(_scratch);

        IntegrityFix fix = await new RegistryIntegrityFixer(scanner, Fixtures.Cleaner(store, Fixtures.Guard(), _firewall))
            .FixAsync(report.Issues, createRestorePoint: false, cancellationToken: TestContext.Current.CancellationToken);

        TransactionRecord transaction = fix.Result.Transaction;
        Assert.Equal(TransactionKind.RegistryIntegrity, transaction.Kind);
        Assert.All(transaction.Items, i => Assert.Equal(RemovalStatus.Removed, i.Status));
        Assert.Null(Open(@"Classes\Nsk.Broken\shell\open"));
        Assert.Null(Open(@"Services\NskGone"));
        Assert.Null(Open(@"Classes\.nskgone")!.GetValue(string.Empty));
        Assert.DoesNotContain(_firewall.Rules, r => r.Name == "Nsk Gone");
        string export = File.ReadAllText(store.RegistryExportPath(transaction.Id));
        Assert.StartsWith("Windows Registry Editor Version 5.00", export, StringComparison.Ordinal);
        Assert.Contains(@"Nsk.Broken\shell\open\command]", export, StringComparison.Ordinal);
        Assert.Empty(scanner.Scan(cancellationToken: TestContext.Current.CancellationToken).Issues);

        RestoreReport restored = new TransactionRestorer(store, _firewall, new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(transaction.Id)!;

        Assert.Empty(restored.Problems);
        Assert.Equal(report.Issues.Select(i => i.Path), scanner.Scan(cancellationToken: TestContext.Current.CancellationToken).Issues.Select(i => i.Path));
    }

    [Fact]
    public async Task An_issue_that_healed_since_the_scan_is_not_touched()
    {
        RegistryIntegrityScanner scanner = Scanner();
        IntegrityIssue issue = scanner.Scan([IntegrityIssueKind.FileAssociation], TestContext.Current.CancellationToken).Issues
            .Single(i => i.ItemKind == LeftoverKind.RegistryKey);
        _scratch.File(@"gone\app.exe");

        IntegrityFix fix = await new RegistryIntegrityFixer(scanner, Fixtures.Cleaner(Fixtures.Store(_scratch), Fixtures.Guard(), _firewall))
            .FixAsync([issue], createRestorePoint: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([issue], fix.NoLongerBroken);
        Assert.NotNull(Open(@"Classes\Nsk.Broken\shell\open\command"));
    }

    [Fact]
    public async Task Keys_the_registry_guard_protects_are_reported_but_never_fixed()
    {
        var rules = new RegistryRuleSet(
            DefaultRegistryRules.BuildRules().Append(new RegistryRule(RegistryRoot.User, [.. $@"{_key}\Classes\Nsk.Broken".Split('\\')], ProtectionKind.Tree, "test")),
            DefaultRegistryRules.BuildProtectedValues());
        var guard = new RegistryGuard(rules, new RegistryServiceFacts(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var scanner = new RegistryIntegrityScanner(_locations, guard, _firewall, SystemCommandEnvironment.Instance, NullLogger<RegistryIntegrityScanner>.Instance);

        IntegrityIssue issue = scanner.Scan([IntegrityIssueKind.FileAssociation], TestContext.Current.CancellationToken).Issues
            .Single(i => i.ItemKind == LeftoverKind.RegistryKey);
        IntegrityFix fix = await new RegistryIntegrityFixer(scanner, Fixtures.Cleaner(Fixtures.Store(_scratch), Fixtures.Guard(), _firewall))
            .FixAsync([issue], createRestorePoint: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(issue.Fixable);
        Assert.Contains("ProtectedTree", issue.Protection, StringComparison.Ordinal);
        Assert.Empty(fix.Fixed);
        Assert.NotNull(Open(@"Classes\Nsk.Broken\shell\open\command"));
    }

    public void Dispose()
    {
        Hives.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        _scratch.Dispose();
    }

    private RegistryIntegrityScanner Scanner() =>
        new(_locations, RegistryGuard.ForCurrentMachine(), _firewall, SystemCommandEnvironment.Instance, NullLogger<RegistryIntegrityScanner>.Instance);

    private string Hkcu(string relative) => $@"HKCU\{_key}\{relative}";

    private RegistryKey? Open(string relative) => Hives.CurrentUser.OpenSubKey($@"{_key}\{relative}");

    private void Set(string relative, string name, object value)
    {
        using RegistryKey key = Hives.CurrentUser.CreateSubKey($@"{_key}\{relative}");
        key.SetValue(name, value);
    }
}
