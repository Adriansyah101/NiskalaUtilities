using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Guardian.Settings;
using Niskala.Guardian.Startup;
using Niskala.Interop.Firewall;
using Niskala.Interop.Processes;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;
using Niskala.Optimizer.Processes;
using Niskala.Optimizer.Resources;
using Niskala.Scanner.Sizing;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Optimizer.Tests;

internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Folder = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
        Key = $@"{TestRoots.Key}\{Guid.NewGuid():N}";
    }

    public string Folder { get; }

    public string Key { get; }

    public string Hkcu(string relative) => $@"HKCU\{Key}\{relative}";

    public string File(string relative, int bytes = 16)
    {
        string path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Set(string relative, string name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
    {
        using RegistryKey key = Hives.CurrentUser.CreateSubKey($@"{Key}\{relative}");
        if (kind == RegistryValueKind.Unknown)
        {
            key.SetValue(name, value);
        }
        else
        {
            key.SetValue(name, value, kind);
        }
    }

    public object? Get(string relative, string name)
    {
        using RegistryKey? key = Hives.CurrentUser.OpenSubKey($@"{Key}\{relative}");
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public RegistryValueKind? Kind(string relative, string name)
    {
        using RegistryKey? key = Hives.CurrentUser.OpenSubKey($@"{Key}\{relative}");
        return key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true ? key.GetValueKind(name) : null;
    }

    public void Dispose()
    {
        Hives.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}

internal sealed class FakePrograms : IProgramFacts
{
    public HashSet<string> Microsoft { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProgramInfo Describe(string path) =>
        new(path, System.IO.File.Exists(path), System.IO.File.Exists(path) ? new FileInfo(path).Length : 0, "Acme", Microsoft.Contains(path), DiskKind.Ssd);
}

internal sealed class FakeServiceStart : IServiceStartControl
{
    public Dictionary<string, ServiceStartType> Starts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TryGetStartConfig(string name, out ServiceStartConfig config)
    {
        config = new ServiceStartConfig(Starts.GetValueOrDefault(name, ServiceStartType.Manual), false);
        return 0;
    }

    public int TrySetStartType(string name, ServiceStartType startType)
    {
        Starts[name] = startType;
        return 0;
    }
}

internal sealed class FakeTasks : ITaskSource, ITaskStateControl
{
    public Dictionary<string, string> Xml { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TaskEnumeration Enumerate() => new([.. Xml.Select(t => new ScheduledTaskXml(t.Key, t.Value))], 0, 0);

    public int TryGetEnabled(string path, out bool enabled)
    {
        enabled = Enabled.GetValueOrDefault(path, true);
        return 0;
    }

    public int TrySetEnabled(string path, bool enabled)
    {
        Enabled[path] = enabled;
        return 0;
    }

    public static string Definition(string command, bool enabled = true, string trigger = "LogonTrigger") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><{trigger}><Enabled>true</Enabled></{trigger}></Triggers>
          <Settings><Enabled>{(enabled ? "true" : "false")}</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>--quiet</Arguments></Exec></Actions>
        </Task>
        """;
}

internal sealed class NoRestorePoints : IRestorePointService
{
    public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RestorePointRecord(false, null, description, "not requested"));
}

internal sealed class NoFirewall : IFirewallEditor
{
    public int TryList(out List<FirewallRule> rules)
    {
        rules = [];
        return 0;
    }

    public int TryRemove(string name) => 0;

    public int TryAdd(FirewallRule rule) => 0;
}

internal sealed class NoTaskEditor : IScheduledTaskEditor
{
    public int TryRead(string path, out string? xml)
    {
        xml = null;
        return TaskSchedulerNative.NotFound;
    }

    public int TryDelete(string path) => 0;

    public int TryRegister(string path, string xml) => 0;
}

internal sealed class NoServices : IServiceControl
{
    public int TryQuery(string name, out ServiceState state)
    {
        state = ServiceState.NotFound;
        return 0;
    }

    public int TryStop(string name, TimeSpan timeout) => 0;

    public int TryDelete(string name) => 0;
}

internal sealed class Rig
{
    public Rig(Sandbox sandbox)
    {
        Store = new TransactionStore(Path.Combine(sandbox.Folder, "journal"));
        Services = new FakeServiceStart();
        Tasks = new FakeTasks();
        Programs = new FakePrograms();
        Editor = new SettingsEditor(RegistryGuard.ForCurrentMachine(), Store, Services, Tasks, NullLogger<SettingsEditor>.Instance);
        var guard = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);
        var cleaner = new LeftoverCleaner(
            guard,
            RegistryGuard.ForCurrentMachine(),
            Store,
            new NoRestorePoints(),
            new NoFirewall(),
            new NoTaskEditor(),
            new NoServices(),
            SystemCommandEnvironment.Instance,
            new EmptyParentFinder([], guard),
            NullLogger<LeftoverCleaner>.Instance);
        StartupFolder = Directory.CreateDirectory(Path.Combine(sandbox.Folder, "Startup")).FullName;
        Locations = new ResourceLocations
        {
            Startup =
            [
                new StartupLocation(StartupKind.RunValue, InstallScope.User, false, sandbox.Hkcu("Run"), sandbox.Hkcu(@"Approved\Run")),
                new StartupLocation(StartupKind.StartupFolder, InstallScope.User, false, StartupFolder, sandbox.Hkcu(@"Approved\StartupFolder")),
            ],
            RunOnce = [new RunOnceLocation(sandbox.Hkcu("RunOnce"), InstallScope.User)],
            ServicesKey = sandbox.Hkcu("Services"),
            ClassesRoots = [sandbox.Hkcu("Classes")],
            BlockedKeys = [sandbox.Hkcu("Blocked")],
            BlockTarget = sandbox.Hkcu("Blocked"),
        };
        Control = new ResourceControl(
            Locations,
            new StartupCatalog(Locations.Startup, cleaner, SystemCommandEnvironment.Instance),
            RegistryGuard.ForCurrentMachine(),
            Editor,
            Store,
            Tasks,
            Programs,
            SystemCommandEnvironment.Instance);
    }

    public TransactionStore Store { get; }

    public FakeServiceStart Services { get; }

    public FakeTasks Tasks { get; }

    public FakePrograms Programs { get; }

    public SettingsEditor Editor { get; }

    public string StartupFolder { get; }

    public ResourceLocations Locations { get; }

    public ResourceControl Control { get; }

    public Guardian.Restore.TransactionRestorer Restorer() =>
        new(Store, new NoFirewall(), new NoTaskEditor(), NullLogger<Guardian.Restore.TransactionRestorer>.Instance, settings: Editor);
}

internal sealed class FakeProcesses : IProcessSystem
{
    public int CurrentProcessId { get; set; } = 500;

    public string WindowsDirectory { get; set; } = @"C:\Windows";

    public IReadOnlyList<string> OwnDirectories { get; set; } = [@"C:\Tools\Niskala"];

    public List<ProcessEntry> Processes { get; } = [new(4, "System", 0), new(300, "explorer.exe", 4), new(400, "Terminal.exe", 300), new(500, "niskala.exe", 400)];

    public Dictionary<int, string> Images { get; } = new() { [300] = @"C:\Windows\explorer.exe", [400] = @"C:\Apps\Terminal.exe", [500] = @"C:\Tools\Niskala\niskala.exe" };

    public Dictionary<int, int> Sessions { get; } = new() { [300] = 1, [400] = 1, [500] = 1 };

    public Dictionary<int, bool?> Critical { get; } = [];

    public Dictionary<int, DateTime> Started { get; } = [];

    public Dictionary<int, PriorityClass> Priorities { get; } = [];

    public HashSet<int> Suspended { get; } = [];

    public HashSet<int> Efficient { get; } = [];

    public HashSet<int> Windowed { get; } = [];

    public void Add(int id, string image, int session = 1, int parent = 300)
    {
        Processes.Add(new ProcessEntry(id, Path.GetFileName(image), parent));
        Images[id] = image;
        Sessions[id] = session;
        Started[id] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(id);
    }

    public IReadOnlyList<ProcessEntry> Snapshot() => Processes;

    public IReadOnlySet<int> ProcessesWithVisibleWindows() => Windowed;

    public string? ImagePath(int processId) => Images.GetValueOrDefault(processId);

    public int? SessionId(int processId) => Sessions.TryGetValue(processId, out int session) ? session : null;

    public bool? IsCritical(int processId) => Critical.TryGetValue(processId, out bool? critical) ? critical : false;

    public ProcessTimes? Times(int processId) => Started.TryGetValue(processId, out DateTime started) ? new ProcessTimes(started, TimeSpan.Zero) : null;

    public PriorityClass? Priority(int processId) => Priorities.GetValueOrDefault(processId, PriorityClass.Normal);

    public int Suspend(int processId) => Suspended.Add(processId) ? 0 : 1;

    public int Resume(int processId) => Suspended.Remove(processId) ? 0 : 1;

    public int SetEfficiency(int processId, bool efficient)
    {
        if (efficient)
        {
            Efficient.Add(processId);
        }
        else
        {
            Efficient.Remove(processId);
        }

        return 0;
    }

    public int SetPriority(int processId, PriorityClass priority)
    {
        Priorities[processId] = priority;
        return 0;
    }
}
