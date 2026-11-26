using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Firewall;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Leftovers;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Guardian.Tests;

internal sealed class ScratchFolder : IDisposable
{
    private readonly List<string> _junctions = [];

    public ScratchFolder() => Root = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;

    public string Root { get; }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string File(string relative, int bytes = 8)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string Junction(string relative, string target)
    {
        string link = Path.Combine(Root, relative);
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string junction in _junctions.Where(Directory.Exists))
        {
            Directory.Delete(junction, recursive: false);
        }

        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class RegistryScratch : IDisposable
{
    private const int RegOptionCreateLink = 2;
    private const int KeyAllAccess = 0xF003F;
    private const int RegLink = 6;

    public RegistryScratch() => Key = $@"{TestRoots.Key}\{Guid.NewGuid():N}";

    public string Key { get; }

    public string Path(string relative = "") => relative.Length == 0 ? $@"HKCU\{Key}" : $@"HKCU\{Key}\{relative}";

    public RegistryKey Create(string relative) => Hives.CurrentUser.CreateSubKey($@"{Key}\{relative}");

    public void Link(string relative, string target)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        int error = RegCreateKeyExW(Hives.CurrentUser.Handle, $@"{Key}\{relative}", 0, null, RegOptionCreateLink, KeyAllAccess, 0, out SafeRegistryHandle link, out _);
        Assert.Equal(0, error);
        using (link)
        {
            byte[] data = Encoding.Unicode.GetBytes($@"\REGISTRY\USER\{sid}\{Key}\{target}");
            Assert.Equal(0, RegSetValueExW(link, "SymbolicLinkValue", 0, RegLink, data, data.Length));
        }
    }

    public void Dispose()
    {
        if (RegistryNative.TryOpenNoFollow(Hives.CurrentUser.Handle, Key, registry32: false, writable: true, out SafeRegistryHandle key) == 0)
        {
            using (key)
            {
                Delete(key);
            }
        }

    }

    private static void Delete(SafeRegistryHandle key)
    {
        if (!RegistryNative.IsLink(key) && RegistryNative.TryListSubKeys(key, out List<string> names) == 0)
        {
            foreach (string name in names)
            {
                if (RegistryNative.TryOpenNoFollow(key, name, registry32: false, writable: true, out SafeRegistryHandle child) == 0)
                {
                    using (child)
                    {
                        Delete(child);
                    }
                }
            }
        }

        RegistryNative.TryDeleteKey(key);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, int reserved, string? className, int options, int access, nint security, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);
}

internal sealed class FakeRestorePoints(bool created = true) : IRestorePointService
{
    public List<string> Requests { get; } = [];

    public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        Requests.Add(description);
        return Task.FromResult(new RestorePointRecord(created, created ? 42 : null, description, created ? null : "skipped"));
    }
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
    public Dictionary<string, ServiceState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    public int TryQuery(string name, out ServiceState state)
    {
        state = States.GetValueOrDefault(name, ServiceState.NotFound);
        return 0;
    }

    public int TryStop(string name, TimeSpan timeout)
    {
        Calls.Add("stop " + name);
        States[name] = ServiceState.Stopped;
        return 0;
    }

    public int TryDelete(string name)
    {
        Calls.Add("delete " + name);
        States.Remove(name);
        return 0;
    }
}

internal static class Guards
{
    public static PathGuard Real(params string[] protectedTrees) => new(
        ProtectedLocations.Create([.. protectedTrees.Select(p => new ProtectedLocation(p, ProtectionKind.Tree, "test"))], Win32PathResolver.Instance),
        Win32PathResolver.Instance);

    public static InstalledApp App(string name = "Acme Photo Editor") => new()
    {
        Id = "reg:hkcu:" + name.Replace(" ", string.Empty, StringComparison.Ordinal),
        Sources = AppSources.Registry,
        Scope = InstallScope.User,
        DisplayName = name,
    };

    public static LeftoverItem Item(LeftoverKind kind, string path, string? valueName = null) =>
        new(kind, path, valueName, 90, ConfidenceTier.Safe, []);

    public static LeftoverCleaner Cleaner(
        TransactionStore store,
        PathGuard guard,
        IEnumerable<string>? roots = null,
        FakeRestorePoints? points = null,
        FakeFirewall? firewall = null,
        FakeServices? services = null,
        FakeTaskEditor? tasks = null) => new(
        guard,
        RegistryGuard.ForCurrentMachine(),
        store,
        points ?? new FakeRestorePoints(),
        firewall ?? new FakeFirewall(),
        tasks ?? new FakeTaskEditor(),
        services ?? new FakeServices(),
        SystemCommandEnvironment.Instance,
        new EmptyParentFinder(roots ?? [], guard),
        NullLogger<LeftoverCleaner>.Instance);
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

internal static class RebootQueue
{
    private const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string PendingValue = "PendingFileRenameOperations";

    public static int Forget(string root)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return 0;
        }

        using RegistryKey? key = Hives.LocalMachine.OpenSubKey(SessionManager, writable: true);
        if (key?.GetValue(PendingValue) is not string[] entries || entries.Length % 2 != 0)
        {
            return 0;
        }

        var kept = new List<string>(entries.Length);
        for (int i = 0; i + 1 < entries.Length; i += 2)
        {
            if (!entries[i].Contains(root, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(entries[i]);
                kept.Add(entries[i + 1]);
            }
        }

        int forgotten = (entries.Length - kept.Count) / 2;
        if (forgotten == 0)
        {
            return 0;
        }

        if (kept.Count == 0)
        {
            key.DeleteValue(PendingValue);
        }
        else
        {
            key.SetValue(PendingValue, kept.ToArray(), RegistryValueKind.MultiString);
        }

        return forgotten;
    }
}
