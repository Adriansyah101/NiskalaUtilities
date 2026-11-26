using Microsoft.Win32;
using Niskala.Core.Models;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Guardian.Settings;
using Niskala.Interop.Services;
using Niskala.Optimizer.Resources;
using Niskala.Scanner.Sizing;

namespace Niskala.Optimizer.Tests;

public sealed class ResourceControlTests : IDisposable
{
    private const string ShellClsid = "{7A0C9E21-3D4B-4F5A-8C6D-1E2F3A4B5C6D}";

    private readonly Sandbox _sandbox = new();
    private readonly Rig _rig;
    private readonly string _program;

    public ResourceControlTests()
    {
        _rig = new Rig(_sandbox);
        _program = _sandbox.File(@"Acme\acme.exe", 4096);
    }

    [Fact]
    public void A_run_entry_is_switched_off_through_the_journal_and_restore_switches_it_back_on()
    {
        _sandbox.Set("Run", "Acme", $"\"{_program}\" --tray");
        ResourceItem item = Single(ResourceKind.RunEntry);
        Assert.True(item.Enabled);
        Assert.Equal(BootImpact.Low, item.Impact);

        ResourceChange change = _rig.Control.SetEnabled(item, enabled: false)!;

        Assert.True(change.Succeeded);
        Assert.Equal(TransactionKind.ResourceControl, change.Transaction.Kind);
        Assert.Null(change.Setting.ValueBefore);
        Assert.Equal(0x03, ((byte[])_sandbox.Get(@"Approved\Run", "Acme")!)[0]);
        Assert.False(Single(ResourceKind.RunEntry).Enabled);
        Assert.Equal($"\"{_program}\" --tray", _sandbox.Get("Run", "Acme"));

        RestoreReport report = _rig.Restorer().Restore(change.Transaction.Id)!;

        Assert.Empty(report.Problems);
        Assert.Null(_sandbox.Get(@"Approved\Run", "Acme"));
        Assert.True(Single(ResourceKind.RunEntry).Enabled);
    }

    [Fact]
    public void Startup_folder_shortcuts_use_the_folder_approval_key()
    {
        _sandbox.File(@"Startup\Acme.cmd");
        ResourceItem item = Single(ResourceKind.StartupFolder);

        _rig.Control.SetEnabled(item, enabled: false);

        Assert.Equal("Acme", item.Name);
        Assert.Equal(0x03, ((byte[])_sandbox.Get(@"Approved\StartupFolder", "Acme.cmd")!)[0]);
    }

    [Fact]
    public void A_run_once_entry_is_parked_in_the_journal_and_put_back_exactly()
    {
        _sandbox.Set("RunOnce", "!AcmeSetup", $"\"{_program}\" /finish", RegistryValueKind.ExpandString);
        ResourceItem item = Single(ResourceKind.RunOnceEntry);

        _rig.Control.SetEnabled(item, enabled: false);
        ResourceItem parked = Single(ResourceKind.RunOnceEntry);

        Assert.Null(_sandbox.Get("RunOnce", "!AcmeSetup"));
        Assert.False(parked.Enabled);
        Assert.Equal("AcmeSetup", parked.Name);

        _rig.Control.SetEnabled(parked, enabled: true);

        Assert.Equal($"\"{_program}\" /finish", _sandbox.Get("RunOnce", "!AcmeSetup"));
        Assert.Equal(RegistryValueKind.ExpandString, _sandbox.Kind("RunOnce", "!AcmeSetup"));
        Assert.True(Assert.Single(_rig.Control.List([ResourceKind.RunOnceEntry])).Enabled);
    }

    [Fact]
    public void A_third_party_service_is_disabled_and_enabled_back_to_its_old_start_type()
    {
        _sandbox.Set(@"Services\NskTestSvc", "Type", 16);
        _sandbox.Set(@"Services\NskTestSvc", "Start", 2);
        _sandbox.Set(@"Services\NskTestSvc", "ImagePath", $"\"{_program}\" -service", RegistryValueKind.ExpandString);
        _sandbox.Set(@"Services\NskTestSvc", "DisplayName", "Acme Updater");
        _sandbox.Set(@"Services\NskDriver", "Type", 1);
        _sandbox.Set(@"Services\NskDriver", "Start", 1);
        _sandbox.Set(@"Services\NskDriver", "ImagePath", _program);
        _rig.Services.Starts["NskTestSvc"] = ServiceStartType.Automatic;
        ResourceItem item = Single(ResourceKind.Service);
        Assert.Equal(("Acme Updater", BootImpact.Low, true), (item.Name, item.Impact, item.CanToggle));

        _rig.Control.SetEnabled(item, enabled: false);
        Assert.Equal(ServiceStartType.Disabled, _rig.Services.Starts["NskTestSvc"]);
        _sandbox.Set(@"Services\NskTestSvc", "Start", 4);

        _rig.Control.SetEnabled(Single(ResourceKind.Service), enabled: true);

        Assert.Equal(ServiceStartType.Automatic, _rig.Services.Starts["NskTestSvc"]);
    }

    [Fact]
    public void Boot_and_system_services_are_listed_as_locked_by_the_registry_guard()
    {
        _sandbox.Set(@"Services\Tcpip", "Type", 16);
        _sandbox.Set(@"Services\Tcpip", "Start", 2);
        _sandbox.Set(@"Services\Tcpip", "ImagePath", _program);

        ResourceItem item = Single(ResourceKind.Service);

        Assert.False(item.CanToggle);
        Assert.Contains("ServiceBootOrSystemStart", item.Blocked, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => _rig.Control.SetEnabled(item, enabled: false));
        Assert.Empty(_rig.Services.Starts);
    }

    [Fact]
    public void Microsoft_programs_and_tasks_stay_out_of_the_list()
    {
        string signed = _sandbox.File(@"Vendor\update.exe");
        _rig.Programs.Microsoft.Add(signed);
        _rig.Tasks.Xml[@"\Microsoft\Windows\Defrag\ScheduledDefrag"] = FakeTasks.Definition(_program);
        _rig.Tasks.Xml[@"\VendorUpdate"] = FakeTasks.Definition(signed);
        _rig.Tasks.Xml[@"\AcmeAtLogon"] = FakeTasks.Definition(_program);
        _rig.Tasks.Xml[@"\AcmeNightly"] = FakeTasks.Definition(_program, enabled: false, trigger: "CalendarTrigger");

        IReadOnlyList<ResourceItem> tasks = _rig.Control.List([ResourceKind.ScheduledTask]);

        Assert.Equal([(@"\AcmeAtLogon", true, true), (@"\AcmeNightly", false, false)], tasks.Select(t => (t.Target, t.Enabled, t.RunsAtSignIn)));
        Assert.Equal([BootImpact.Low, BootImpact.None], tasks.Select(t => t.Impact));
    }

    [Fact]
    public void A_scheduled_task_switch_is_journaled_and_reverted()
    {
        _rig.Tasks.Xml[@"\AcmeAtLogon"] = FakeTasks.Definition(_program);
        ResourceChange change = _rig.Control.SetEnabled(Single(ResourceKind.ScheduledTask), enabled: false)!;

        Assert.False(_rig.Tasks.Enabled[@"\AcmeAtLogon"]);
        Assert.Equal(true, change.Setting.EnabledBefore);

        _rig.Restorer().Restore(change.Transaction.Id);

        Assert.True(_rig.Tasks.Enabled[@"\AcmeAtLogon"]);
    }

    [Fact]
    public void Context_menu_handlers_are_switched_through_the_blocked_list()
    {
        string server = _sandbox.File(@"Acme\shellext.dll");
        _sandbox.Set(@"Classes\*\shellex\ContextMenuHandlers\AcmeMenu", string.Empty, ShellClsid);
        _sandbox.Set(@"Classes\Directory\shellex\ContextMenuHandlers\AcmeMenu", string.Empty, ShellClsid);
        _sandbox.Set($@"Classes\CLSID\{ShellClsid}\InprocServer32", string.Empty, server);
        ResourceItem item = Single(ResourceKind.ContextMenu);
        Assert.Equal(("AcmeMenu", "*, Directory", BootImpact.None), (item.Name, item.Location, item.Impact));

        _rig.Control.SetEnabled(item, enabled: false);
        Assert.NotNull(_sandbox.Get("Blocked", ShellClsid));
        Assert.False(Single(ResourceKind.ContextMenu).Enabled);

        _rig.Control.SetEnabled(Single(ResourceKind.ContextMenu), enabled: true);
        Assert.Null(_sandbox.Get("Blocked", ShellClsid));
    }

    [Fact]
    public void Switching_to_the_current_state_changes_nothing()
    {
        _sandbox.Set("Run", "Acme", _program);

        Assert.Null(_rig.Control.SetEnabled(Single(ResourceKind.RunEntry), enabled: true));
        Assert.Empty(_rig.Store.List());
    }

    [Fact]
    public void A_restore_leaves_a_value_alone_when_it_changed_again_since()
    {
        _sandbox.Set("Run", "Acme", _program);
        ResourceChange change = _rig.Control.SetEnabled(Single(ResourceKind.RunEntry), enabled: false)!;
        _sandbox.Set(@"Approved\Run", "Acme", new byte[12], RegistryValueKind.Binary);

        RestoreReport report = _rig.Restorer().Restore(change.Transaction.Id)!;

        Assert.Contains(report.Problems, p => p.Contains("changed since", StringComparison.Ordinal));
        Assert.Equal(SettingStatus.Skipped, report.Transaction.Settings[0].Status);
    }

    [Theory]
    [InlineData(ResourceKind.RunEntry, true, 512L * 1024, DiskKind.Ssd, null, false, true, BootImpact.Low)]
    [InlineData(ResourceKind.RunEntry, true, 30L << 20, DiskKind.Ssd, null, false, true, BootImpact.Medium)]
    [InlineData(ResourceKind.RunEntry, true, 30L << 20, DiskKind.Hdd, null, false, true, BootImpact.High)]
    [InlineData(ResourceKind.RunEntry, false, 90L << 20, DiskKind.Ssd, null, false, true, BootImpact.None)]
    [InlineData(ResourceKind.RunOnceEntry, true, 90L << 20, DiskKind.Hdd, null, false, true, BootImpact.Low)]
    [InlineData(ResourceKind.Service, true, 30L << 20, DiskKind.Ssd, ServiceStartType.Automatic, false, true, BootImpact.High)]
    [InlineData(ResourceKind.Service, true, 30L << 20, DiskKind.Ssd, ServiceStartType.Automatic, true, true, BootImpact.Low)]
    [InlineData(ResourceKind.Service, true, 90L << 20, DiskKind.Ssd, ServiceStartType.Manual, false, true, BootImpact.None)]
    [InlineData(ResourceKind.ScheduledTask, true, 90L << 20, DiskKind.Ssd, null, false, false, BootImpact.None)]
    public void Boot_impact_follows_size_disk_and_start_mode(ResourceKind kind, bool enabled, long bytes, DiskKind disk, ServiceStartType? start, bool delayed, bool atSignIn, BootImpact expected)
    {
        var program = new ProgramInfo(@"C:\Apps\x.exe", true, bytes, null, false, disk);

        Assert.Equal(expected, BootImpactEstimator.Estimate(kind, enabled, program, start, delayed, atSignIn));
    }

    [Fact]
    public void A_missing_program_has_an_unknown_impact() =>
        Assert.Equal(BootImpact.Unknown, BootImpactEstimator.Estimate(ResourceKind.RunEntry, true, new ProgramInfo(@"C:\Gone\x.exe", false, 0, null, false, DiskKind.Unknown)));

    public void Dispose() => _sandbox.Dispose();

    private ResourceItem Single(ResourceKind kind) => Assert.Single(_rig.Control.List([kind]));
}
