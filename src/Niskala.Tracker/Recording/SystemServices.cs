using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Firewall;
using Niskala.Interop.Processes;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;
using Niskala.Tracker.Logs;

namespace Niskala.Tracker.Recording;

public sealed record ServiceSnapshot(IReadOnlySet<string> Services, IReadOnlyList<FirewallRule>? Rules);

public interface ISystemServices
{
    ServiceSnapshot Capture();

    string? ServiceImage(string name);

    int? ServicesProcessId();

    int? InstallerServiceProcessId();

    string? ImagePath(int processId);
}

public sealed class SystemServices(ICommandEnvironment environment) : ISystemServices
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public ServiceSnapshot Capture()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RegistryNative.TryOpenNoFollow(RegistryNative.Hive(RegistryHive.LocalMachine), ServicesKey, registry32: false, writable: false, out SafeRegistryHandle key) == 0)
        {
            using (key)
            {
                if (RegistryNative.TryListSubKeys(key, out List<string> names) == 0)
                {
                    services.UnionWith(names);
                }
            }
        }
        else
        {
            key.Dispose();
        }

        return new ServiceSnapshot(services, FirewallNative.TryList(out List<FirewallRule> rules) == 0 ? rules : null);
    }

    public string? ServiceImage(string name) =>
        ServiceImagePath.Resolve(RegistryNative.TryReadString(RegistryNative.Hive(RegistryHive.LocalMachine), $@"{ServicesKey}\{name}", "ImagePath"), environment.WindowsDirectory);

    public int? ServicesProcessId() =>
        ProcessNative.TrySnapshot(out List<ProcessEntry> processes) == 0
            ? processes.Where(p => string.Equals(p.ExeName, "services.exe", StringComparison.OrdinalIgnoreCase)).Select(p => (int?)p.Id).FirstOrDefault()
            : null;

    public int? InstallerServiceProcessId() => ServiceNative.TryGetProcessId("msiserver");

    public string? ImagePath(int processId) => ProcessNative.TryGetImagePath(processId);
}

public static class ServiceChanges
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    public static (IReadOnlyList<KeyChange> Services, IReadOnlyList<FirewallRuleChange> Rules) Find(
        ServiceSnapshot before,
        ServiceSnapshot after,
        IReadOnlyList<FileChange> files,
        ISystemServices services)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(services);

        string[] created = [.. files.Where(f => f.Change == ChangeKind.Created).Select(f => f.Path)];
        bool Owned(string? path) => path is not null && created.Any(c => ChangeReconciler.IsSameOrUnder(path, c));

        List<KeyChange> keys = [.. after.Services
            .Where(s => !before.Services.Contains(s) && Owned(services.ServiceImage(s)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(s => new KeyChange(ServicesKey + s, ChangeKind.Created) { Origin = "service" })];

        List<FirewallRuleChange> rules = before.Rules is null || after.Rules is null ? [] : [.. after.Rules
            .Where(r => !before.Rules.Any(b => Same(b, r)) && Owned(r.ApplicationName))
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new FirewallRuleChange(r.Name, r.ApplicationName))];

        return (keys, rules);
    }

    private static bool Same(FirewallRule a, FirewallRule b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ApplicationName, b.ApplicationName, StringComparison.OrdinalIgnoreCase)
        && a.Direction == b.Direction
        && a.Protocol == b.Protocol;
}
