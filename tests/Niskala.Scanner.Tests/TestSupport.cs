using System.Diagnostics;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Inventory;

namespace Niskala.Scanner.Tests;

internal static class TestEntries
{
    public static RawUninstallEntry Entry(
        string keyName,
        InstallScope scope = InstallScope.Machine,
        RegistryViewKind view = RegistryViewKind.Registry64,
        DateTime? lastWrite = null,
        params (string Name, object? Value)[] values)
    {
        string root = (scope, view) switch
        {
            (InstallScope.Machine, RegistryViewKind.Registry32) => @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            (InstallScope.Machine, _) => @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            _ => @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        return new RawUninstallEntry
        {
            Scope = scope,
            View = view,
            KeyPath = $@"{root}\{keyName}",
            KeyName = keyName,
            LastWriteTimeUtc = lastWrite,
            Values = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static InstalledApp App(string id, string? name = "Acme", string? version = "1.0") => new()
    {
        Id = id,
        Sources = AppSources.Registry,
        Scope = InstallScope.Machine,
        View = RegistryViewKind.Registry64,
        DisplayName = name,
        DisplayVersion = version,
    };

    public static PathGuard ZGuard(params ProtectedLocation[] rules) =>
        new(ProtectedLocations.Create(rules, new IdentityResolver()), new IdentityResolver());
}

internal sealed class IdentityResolver : IPathResolver
{
    public PathResolution Resolve(string normalizedPath) => new(ResolutionStatus.Resolved, normalizedPath, true, false, false, 0);
}

internal sealed class FakeEnvironment(params string[] files) : ICommandEnvironment
{
    private readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => _files.Contains(path);

    public string SystemDirectory => @"C:\Windows\System32";

    public string WindowsDirectory => @"C:\Windows";

    public string ExpandVariables(string text) => text.Replace("%SystemRoot%", WindowsDirectory, StringComparison.OrdinalIgnoreCase);
}

internal sealed class FakeProbe(params string[] nsisFiles) : IExecutableProbe
{
    private readonly HashSet<string> _nsis = new(nsisFiles, StringComparer.OrdinalIgnoreCase);

    public NsisProbeResult ProbeNsis(string executablePath) =>
        _nsis.Contains(executablePath) ? new NsisProbeResult(true, true, 34816) : default;
}

internal sealed class ScratchFolder : IDisposable
{
    private readonly List<string> _junctions = [];

    public ScratchFolder() => Root = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;

    public string Root { get; }

    public string File(string relative, int bytes)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string File(string relative, byte[] content)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
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
        foreach (string junction in _junctions)
        {
            Directory.Delete(junction, recursive: false);
        }

        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class FakeTraceSource : ISystemTraceSource
{
    public IReadOnlyList<ServiceTrace> ServiceList { get; init; } = [];

    public IReadOnlyList<RegistryValueTrace> RunList { get; init; } = [];

    public IReadOnlyList<RegistryValueTrace> AppPathList { get; init; } = [];

    public TaskList TaskList { get; init; } = new([], 0, 0);

    public IReadOnlyList<ShortcutTrace> ShortcutList { get; init; } = [];

    public Dictionary<Guid, Guid> UpgradeCodes { get; } = [];

    public HashSet<Guid> RegisteredProducts { get; } = [];

    public int ServiceReads { get; private set; }

    public IReadOnlyList<ServiceTrace> Services()
    {
        ServiceReads++;
        return ServiceList;
    }

    public IReadOnlyList<RegistryValueTrace> RunEntries() => RunList;

    public IReadOnlyList<RegistryValueTrace> AppPaths() => AppPathList;

    public TaskList ScheduledTasks() => TaskList;

    public IReadOnlyList<ShortcutTrace> Shortcuts() => ShortcutList;

    public Guid? FindUpgradeCode(Guid productCode, InstallScope scope) => UpgradeCodes.TryGetValue(productCode, out Guid code) ? code : null;

    public bool? IsProductRegistered(Guid productCode) => RegisteredProducts.Contains(productCode);
}
