using Niskala.Interop.Firewall;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian;

public interface IFirewallEditor : IFirewallRuleSource
{
    int TryRemove(string name);

    int TryAdd(FirewallRule rule);
}

public sealed class SystemFirewallEditor : IFirewallEditor
{
    public static SystemFirewallEditor Instance { get; } = new();

    public int TryList(out List<FirewallRule> rules) => FirewallNative.TryList(out rules);

    public int TryRemove(string name) => FirewallNative.TryRemove(name);

    public int TryAdd(FirewallRule rule) => FirewallNative.TryAdd(rule);
}

public interface IScheduledTaskEditor
{
    int TryRead(string path, out string? xml);

    int TryDelete(string path);

    int TryRegister(string path, string xml);
}

public sealed class SystemScheduledTaskEditor : IScheduledTaskEditor
{
    public static SystemScheduledTaskEditor Instance { get; } = new();

    public int TryRead(string path, out string? xml) => TaskSchedulerNative.TryRead(path, out xml);

    public int TryDelete(string path) => TaskSchedulerNative.TryDelete(path);

    public int TryRegister(string path, string xml) => TaskSchedulerNative.TryRegister(path, xml);
}

public interface IServiceControl
{
    int TryQuery(string name, out ServiceState state);

    int TryStop(string name, TimeSpan timeout);

    int TryDelete(string name);
}

public sealed class SystemServiceControl : IServiceControl
{
    public static SystemServiceControl Instance { get; } = new();

    public int TryQuery(string name, out ServiceState state) => ServiceNative.TryQuery(name, out state);

    public int TryStop(string name, TimeSpan timeout) => ServiceNative.TryStop(name, timeout);

    public int TryDelete(string name) => ServiceNative.TryDelete(name);
}
