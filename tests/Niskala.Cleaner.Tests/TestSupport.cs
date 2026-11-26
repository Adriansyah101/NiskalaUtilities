using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Cleaner.Rules;
using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Firewall;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;

namespace Niskala.Cleaner.Tests;

internal sealed class ScratchFolder : IDisposable
{
    private readonly List<string> _junctions = [];

    public ScratchFolder() => Root = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;

    public string Root { get; }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string File(string relative, int bytes = 16, DateTime? lastWriteUtc = null)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        if (lastWriteUtc is { } stamp)
        {
            System.IO.File.SetCreationTimeUtc(path, stamp);
            System.IO.File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    public string Junction(string relative, string target)
    {
        string link = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
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

        if (Directory.Exists(Root))
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class FakeCleanEnvironment(ScratchFolder scratch, bool elevated = true) : ICleanEnvironment
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsElevated { get; set; } = elevated;

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public string? Resolve(CleanRoot root) => scratch.Dir(root.ToString());

    public IReadOnlySet<string> RunningProcessNames() => Running;

    public string Folder(CleanRoot root) => Resolve(root)!;
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public List<string> Requests { get; } = [];

    public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        Requests.Add(description);
        return Task.FromResult(new RestorePointRecord(true, 7, description, null));
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
    public int TryQuery(string name, out ServiceState state)
    {
        state = ServiceState.NotFound;
        return 0;
    }

    public int TryStop(string name, TimeSpan timeout) => 0;

    public int TryDelete(string name) => 0;
}

internal sealed class FakeTaskEditor : IScheduledTaskEditor
{
    public int TryRead(string path, out string? xml)
    {
        xml = null;
        return TaskSchedulerNative.NotFound;
    }

    public int TryDelete(string path) => TaskSchedulerNative.NotFound;

    public int TryRegister(string path, string xml) => 0;
}

internal static class Fixtures
{
    public static PathGuard Guard(params string[] protectedTrees) => new(
        ProtectedLocations.Create([.. protectedTrees.Select(p => new ProtectedLocation(p, ProtectionKind.Tree, "test"))], Win32PathResolver.Instance),
        Win32PathResolver.Instance);

    public static TransactionStore Store(ScratchFolder scratch) => new(scratch.Dir("journal"));

    public static CleanupJournal Journal(TransactionStore store, PathGuard guard, FakeRestorePoints? points = null) =>
        new(guard, store, points ?? new FakeRestorePoints(), NullLogger<CleanupJournal>.Instance);

    public static LeftoverCleaner Cleaner(TransactionStore store, PathGuard guard, FakeFirewall? firewall = null) => new(
        guard,
        RegistryGuard.ForCurrentMachine(),
        store,
        new FakeRestorePoints(),
        firewall ?? new FakeFirewall(),
        new FakeTaskEditor(),
        new FakeServices(),
        SystemCommandEnvironment.Instance,
        new EmptyParentFinder([], guard),
        NullLogger<LeftoverCleaner>.Instance);

    public static CleanRule Rule(string id, params TargetDefinition[] targets) => new()
    {
        Id = id,
        Category = CleanCategory.TemporaryFiles,
        Name = new LocalizedText { En = id, Id = id },
        Targets = targets,
    };
}
