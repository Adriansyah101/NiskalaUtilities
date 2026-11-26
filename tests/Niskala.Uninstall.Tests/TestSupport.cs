using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Firewall;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Packages;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Security;
using Niskala.Uninstall.Verification;

namespace Niskala.Uninstall.Tests;

internal sealed class FakeEnvironment(params string[] files) : ICommandEnvironment
{
    private readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => _files.Contains(path);

    public string SystemDirectory => @"C:\Windows\System32";

    public string WindowsDirectory => @"C:\Windows";

    public string ExpandVariables(string text) => text
        .Replace("%SystemRoot%", WindowsDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("%LOCALAPPDATA%", @"C:\Users\u\AppData\Local", StringComparison.OrdinalIgnoreCase);
}

internal sealed class FakeLocations(LocationTrust trust = LocationTrust.Trusted) : IWritableLocationProbe
{
    public List<string> Checked { get; } = [];

    public LocationCheck Check(string file)
    {
        Checked.Add(file);
        return new LocationCheck(trust, Path.GetDirectoryName(file), trust == LocationTrust.UserWritable ? @"BUILTIN\Users (S-1-5-32-545)" : null);
    }
}

internal sealed class FakePreflightEnvironment(bool elevated = true, bool busy = false, params RunningProcess[] processes) : IPreflightEnvironment
{
    public bool Busy { get; set; } = busy;

    public bool IsElevated => elevated;

    public bool IsInstallerBusy() => Busy;

    public IReadOnlyList<RunningProcess> RunningProcesses() => processes;
}

internal sealed class IdentityResolver : IPathResolver
{
    public PathResolution Resolve(string normalizedPath) => new(ResolutionStatus.Resolved, normalizedPath, true, false, false, 0);
}

internal static class TestApps
{
    public static readonly PathGuard Guard = new(
        ProtectedLocations.Create([new ProtectedLocation(@"C:\Program Files", ProtectionKind.Self, "program files")], new IdentityResolver()),
        new IdentityResolver());

    public static InstalledApp App(InstallerKind kind, string? uninstall = null, string? quiet = null, ICommandEnvironment? environment = null)
    {
        environment ??= new FakeEnvironment();
        return new InstalledApp
        {
            Id = "reg:hklm64:Acme",
            Sources = AppSources.Registry,
            Scope = InstallScope.Machine,
            View = RegistryViewKind.Registry64,
            KeyName = "Acme",
            DisplayName = "Acme",
            InstallLocation = @"C:\Program Files\Acme",
            InstallerKind = kind,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            UninstallCommand = uninstall is null ? null : CommandLineParser.Parse(uninstall, environment),
            QuietUninstallCommand = quiet is null ? null : CommandLineParser.Parse(quiet, environment),
        };
    }
}

internal sealed class EmptyTraceSource : ISystemTraceSource
{
    public IReadOnlyList<ServiceTrace> Services() => [];

    public IReadOnlyList<RegistryValueTrace> RunEntries() => [];

    public IReadOnlyList<RegistryValueTrace> AppPaths() => [];

    public TaskList ScheduledTasks() => new([], 0, 0);

    public IReadOnlyList<ShortcutTrace> Shortcuts() => [];

    public Guid? FindUpgradeCode(Guid productCode, InstallScope scope) => null;

    public bool? IsProductRegistered(Guid productCode) => null;
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
        return 0;
    }
}

internal sealed class FakeFirewallEditor : IFirewallEditor
{
    public int TryList(out List<FirewallRule> rules)
    {
        rules = [];
        return 0;
    }

    public int TryRemove(string name) => 0;

    public int TryAdd(FirewallRule rule) => 0;
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RestorePointRecord(false, null, description, "test"));
}

internal sealed class NoTrust : IFileTrustProbe
{
    public ExclusionReason? Check(string path, bool isDirectory) => null;
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

internal sealed class FakeRunner(List<string> calls, int? exitCode = 0, int startError = 0, Action? onRun = null) : IUninstallRunner
{
    public ResolvedCommand? Command { get; private set; }

    public string? RegistryKey { get; private set; }

    public Task<RunnerResult> RunAsync(ResolvedCommand command, string? registryKey, IReadOnlyList<string> installRoots, IProgress<JobEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        calls.Add("run");
        Command = command;
        RegistryKey = registryKey;
        onRun?.Invoke();
        return Task.FromResult(startError != 0
            ? new RunnerResult(startError, null)
            : new RunnerResult(0, new UninstallRun(CompletionReason.AllProcessesExited, exitCode, TimeSpan.FromSeconds(1), [], [])));
    }
}

internal sealed class FakePackages(List<string> calls, Action? onRemove = null) : IPackageRemover
{
    public Task<PackageRemovalResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken = default)
    {
        calls.Add("package " + packageFullName);
        onRemove?.Invoke();
        return Task.FromResult(new PackageRemovalResult(0, null, TimeSpan.Zero));
    }
}

internal sealed class RecordingGuardian(List<string> calls) : IGuardianService
{
    public Task<GuardianCheckpoint> PrepareAsync(InstalledApp app, Fingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        calls.Add("guardian");
        return Task.FromResult(new GuardianCheckpoint(false, []));
    }
}

internal sealed class FakeState : IInstallStateProbe
{
    public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Packages { get; } = [];

    public bool? KeyExists(string registryKey) => Keys.Contains(registryKey);

    public bool? ProductRegistered(Guid productCode) => null;

    public bool? PackageExists(string packageFullName) => Packages.Contains(packageFullName);

    public FolderState Folder(string path) => new(false, null, false);
}
