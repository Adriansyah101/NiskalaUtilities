using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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

namespace Niskala.Toolkit.Tests;

internal sealed class ScratchFolder : IDisposable
{
    private readonly List<string> _links = [];

    public ScratchFolder() => Root = Directory.CreateDirectory(System.IO.Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;

    public string Root { get; }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public string File(string relative, byte[] content, DateTime? lastWriteUtc = null)
    {
        string path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        if (lastWriteUtc is { } stamp)
        {
            System.IO.File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    public string Link(string kind, string relative, string target)
    {
        string link = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", kind, link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(System.IO.File.Exists(link) || Directory.Exists(link), $"mklink {kind} failed");
        _links.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link, recursive: false);
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

internal static class Fixtures
{
    public static byte[] Bytes(int length, byte seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + (i * 7));
        }

        return data;
    }

    public static PathGuard Guard(params string[] protectedTrees) => new(
        ProtectedLocations.Create([.. protectedTrees.Select(p => new ProtectedLocation(p, ProtectionKind.Tree, "test"))], Win32PathResolver.Instance),
        Win32PathResolver.Instance);

    public static LeftoverCleaner Cleaner(TransactionStore store, PathGuard guard) => new(
        guard,
        RegistryGuard.ForCurrentMachine(),
        store,
        new NoRestorePoints(),
        new NoFirewall(),
        new NoTaskEditor(),
        new NoServices(),
        SystemCommandEnvironment.Instance,
        new EmptyParentFinder([], guard),
        NullLogger<LeftoverCleaner>.Instance);

    private sealed class NoRestorePoints : IRestorePointService
    {
        public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestorePointRecord(false, null, description, null));
    }

    private sealed class NoFirewall : IFirewallEditor
    {
        public int TryList(out List<FirewallRule> rules)
        {
            rules = [];
            return 0;
        }

        public int TryRemove(string name) => 0;

        public int TryAdd(FirewallRule rule) => 0;
    }

    private sealed class NoTaskEditor : IScheduledTaskEditor
    {
        public int TryRead(string path, out string? xml)
        {
            xml = null;
            return TaskSchedulerNative.NotFound;
        }

        public int TryDelete(string path) => 0;

        public int TryRegister(string path, string xml) => 0;
    }

    private sealed class NoServices : IServiceControl
    {
        public int TryQuery(string name, out ServiceState state)
        {
            state = ServiceState.NotFound;
            return 0;
        }

        public int TryStop(string name, TimeSpan timeout) => 0;

        public int TryDelete(string name) => 0;
    }
}
