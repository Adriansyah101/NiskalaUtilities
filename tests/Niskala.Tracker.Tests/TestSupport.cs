using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Etw;
using Niskala.Interop.Firewall;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Tracing;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Tracker.Tests;

internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder() => Root = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;

    public string Root { get; }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string File(string relative, string text = "data")
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RegistryScratch : IDisposable
{
    public RegistryScratch() => Key = $@"{TestRoots.Key}\{Guid.NewGuid():N}";

    public string Key { get; }

    public string Path(string relative = "") => relative.Length == 0 ? $@"HKCU\{Key}" : $@"HKCU\{Key}\{relative}";

    public string Kernel(string relative = "") => $@"\REGISTRY\USER\{Sid.Current}\{Key}" + (relative.Length == 0 ? string.Empty : "\\" + relative);

    public RegistryKey Create(string relative) => Hives.CurrentUser.CreateSubKey($@"{Key}\{relative}");

    public void Dispose()
    {
        Hives.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
    }
}

internal static class Sid
{
    public static string Current { get; } = WindowsIdentity.GetCurrent().User!.Value;
}

internal static class Kernel
{
    public const string Device = @"\Device\NiskalaTestVolume";

    public static string Drive { get; } = Path.GetPathRoot(Path.GetTempPath())!.TrimEnd('\\');

    public static KernelPathTranslator Translator { get; } = new(new Dictionary<string, string> { [Device] = Drive }, Sid.Current);

    public static string NtFile(string path) => Device + path[Drive.Length..];

    public static EtwEvent Event(EtwEventKind kind, string path, int second, int processId = 100) =>
        new(kind, processId, new DateTime(2026, 11, 21, 10, 0, second, DateTimeKind.Utc), path);
}

internal sealed class FakeState : ISystemState
{
    public Dictionary<string, FileState> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<(string, string), RawRegistryValue> Values { get; } = [];

    public FileState File(string path) => Files.GetValueOrDefault(path, FileState.Missing);

    public bool KeyExists(string key) => Keys.Contains(key);

    public RawRegistryValue? Value(string key, string name) => Values.GetValueOrDefault((key.ToUpperInvariant(), name.ToUpperInvariant()));

    public void Set(string key, string name, int kind, byte[] data) => Values[(key.ToUpperInvariant(), name.ToUpperInvariant())] = new RawRegistryValue(name, kind, data);
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RestorePointRecord(false, null, description, "skipped in tests"));
}

internal sealed class FakeFirewall(params FirewallRule[] rules) : IFirewallEditor
{
    public List<FirewallRule> Rules { get; } = [.. rules];

    public int TryList(out List<FirewallRule> list)
    {
        list = [.. Rules];
        return 0;
    }

    public int TryRemove(string name)
    {
        int index = Rules.FindIndex(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Rules.RemoveAt(index);
        }

        return 0;
    }

    public int TryAdd(FirewallRule rule)
    {
        Rules.Add(rule);
        return 0;
    }

    public static FirewallRule Rule(string name, string? application) =>
        new(name, "test", application, null, 6, "80", null, "*", "*", null, 1, "All", true, null, 7, false, 1);
}

internal sealed class FakeServices : IServiceControl
{
    public int TryQuery(string name, out ServiceState state)
    {
        state = ServiceState.NotFound;
        return 0;
    }

    public int TryStop(string name, TimeSpan timeout) => 0;

    public int TryDelete(string name) => 0;
}

internal static class Guards
{
    public static PathGuard Real(params string[] protectedTrees) => new(
        ProtectedLocations.Create([.. protectedTrees.Select(p => new ProtectedLocation(p, ProtectionKind.Tree, "test"))], Win32PathResolver.Instance),
        Win32PathResolver.Instance);

    public static RegistryGuard Registry(params ProtectedRegistryValue[] values) => new(
        new RegistryRuleSet(DefaultRegistryRules.BuildRules(), [.. DefaultRegistryRules.BuildProtectedValues(), .. values]),
        new NoServices(),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    public static LeftoverCleaner Cleaner(TransactionStore store, PathGuard guard, FakeFirewall? firewall = null, RegistryGuard? registryGuard = null) => new(
        guard,
        registryGuard ?? RegistryGuard.ForCurrentMachine(),
        store,
        new FakeRestorePoints(),
        firewall ?? new FakeFirewall(),
        new FakeTaskEditor(),
        new FakeServices(),
        SystemCommandEnvironment.Instance,
        new EmptyParentFinder([], guard),
        NullLogger<LeftoverCleaner>.Instance);

    public static RawRegistryValue Read(string key, string name)
    {
        Assert.Equal(RegistryVerdict.Allowed, RegistryPath.TryParse(key, RegistryView.Default, out RegistryPath? path));
        Assert.Equal(0, RegistryEraser.TryOpen(path!, writable: false, out SafeRegistryHandle handle));
        using (handle)
        {
            Assert.Equal(0, RegistryNative.TryReadValue(handle, name, out RawRegistryValue? value));
            return value!;
        }
    }

    private sealed class NoServices : IServiceFacts
    {
        public ServiceFacts? TryGet(string serviceName) => null;
    }
}

internal sealed class FakeTaskEditor : IScheduledTaskEditor
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TryRead(string path, out string? xml)
    {
        xml = Tasks.GetValueOrDefault(path);
        return xml is null ? TaskSchedulerNative.NotFound : 0;
    }

    public int TryDelete(string path) => Tasks.Remove(path) ? 0 : TaskSchedulerNative.NotFound;

    public int TryRegister(string path, string xml) => Tasks.TryAdd(path, xml) ? 0 : TaskSchedulerNative.AlreadyExists;
}
