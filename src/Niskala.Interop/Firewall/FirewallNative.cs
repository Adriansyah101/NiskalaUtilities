using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Niskala.Interop.Com;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Niskala.Interop.Firewall;

public sealed record FirewallRule(
    string Name,
    string? Description,
    string? ApplicationName,
    string? ServiceName,
    int Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? LocalAddresses,
    string? RemoteAddresses,
    string? IcmpTypesAndCodes,
    int Direction,
    string? InterfaceTypes,
    bool Enabled,
    string? Grouping,
    int Profiles,
    bool EdgeTraversal,
    int Action);

public static class FirewallNative
{
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolIcmp4 = 1;
    private const int ProtocolIcmp6 = 58;
    private const ushort VtDispatch = 9;

    private static readonly Guid PolicyClass = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly Guid RuleClass = new("2C5BC43E-3369-4C33-AB0C-BE9469677AF4");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static int TryList(out List<FirewallRule> rules)
    {
        rules = [];
        using ComScope com = ComScope.Enter();
        try
        {
            INetFwPolicy2 policy = Create<INetFwPolicy2>(PolicyClass);
            INetFwRules all = policy.GetRules();
            var enumerator = (IEnumVariant)Wrappers.GetOrCreateObjectForComInstance(all.GetNewEnum(), CreateObjectFlags.None);
            while (enumerator.Next(1, out Variant item, out uint fetched) == 0 && fetched == 1)
            {
                if (item.Type != VtDispatch || item.Value == 0)
                {
                    continue;
                }

                var rule = (INetFwRule)Wrappers.GetOrCreateObjectForComInstance(item.Value, CreateObjectFlags.None);
                Marshal.Release(item.Value);
                rules.Add(Read(rule));
                Release(rule);
            }

            Release(enumerator);
            Release(all);
            Release(policy);
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    public static int TryRemove(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Run(rules => rules.Remove(name));
    }

    public static int TryAdd(FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Run(rules =>
        {
            INetFwRule created = Create<INetFwRule>(RuleClass);
            Write(created, rule);
            rules.Add(created);
            Release(created);
        });
    }

    private static int Run(Action<INetFwRules> action)
    {
        using ComScope com = ComScope.Enter();
        try
        {
            INetFwPolicy2 policy = Create<INetFwPolicy2>(PolicyClass);
            INetFwRules rules = policy.GetRules();
            action(rules);
            Release(rules);
            Release(policy);
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    private static FirewallRule Read(INetFwRule rule) => new(
        rule.GetName() ?? string.Empty,
        rule.GetDescription(),
        rule.GetApplicationName(),
        rule.GetServiceName(),
        rule.GetProtocol(),
        rule.GetLocalPorts(),
        rule.GetRemotePorts(),
        rule.GetLocalAddresses(),
        rule.GetRemoteAddresses(),
        rule.GetIcmpTypesAndCodes(),
        rule.GetDirection(),
        rule.GetInterfaceTypes(),
        rule.GetEnabled() != 0,
        rule.GetGrouping(),
        rule.GetProfiles(),
        rule.GetEdgeTraversal() != 0,
        rule.GetAction());

    private static void Write(INetFwRule target, FirewallRule rule)
    {
        target.PutName(rule.Name);
        target.PutDescription(rule.Description);
        target.PutApplicationName(rule.ApplicationName);
        target.PutServiceName(rule.ServiceName);
        target.PutProtocol(rule.Protocol);
        if (rule.Protocol is ProtocolTcp or ProtocolUdp)
        {
            target.PutLocalPorts(rule.LocalPorts);
            target.PutRemotePorts(rule.RemotePorts);
        }

        if (rule.Protocol is ProtocolIcmp4 or ProtocolIcmp6)
        {
            target.PutIcmpTypesAndCodes(rule.IcmpTypesAndCodes);
        }

        target.PutLocalAddresses(rule.LocalAddresses);
        target.PutRemoteAddresses(rule.RemoteAddresses);
        target.PutDirection(rule.Direction);
        target.PutInterfaceTypes(rule.InterfaceTypes);
        target.PutGrouping(rule.Grouping);
        target.PutProfiles(rule.Profiles);
        target.PutEdgeTraversal((short)(rule.EdgeTraversal ? -1 : 0));
        target.PutAction(rule.Action);
        target.PutEnabled((short)(rule.Enabled ? -1 : 0));
    }

    private static unsafe T Create<T>(Guid classId)
    {
        Guid clsid = classId;
        Guid iid = typeof(T).GUID;
        void* pointer;
        HRESULT result = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &pointer);
        Marshal.ThrowExceptionForHR(result.Value);
        object wrapper = Wrappers.GetOrCreateObjectForComInstance((nint)pointer, CreateObjectFlags.None);
        Marshal.Release((nint)pointer);
        return (T)wrapper;
    }

    private static void Release(object wrapper)
    {
        if (wrapper is ComObject com)
        {
            com.FinalRelease();
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Variant
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Extra;
}

[GeneratedComInterface]
[Guid("00020400-0000-0000-C000-000000000046")]
internal partial interface IDispatchSlots
{
    uint GetTypeInfoCount();

    nint GetTypeInfo(uint index, uint locale);

    void GetIDsOfNames(in Guid iid, nint names, uint count, uint locale, nint ids);

    void Invoke(int member, in Guid iid, uint locale, ushort flags, nint parameters, nint result, nint exception, nint argumentError);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
internal partial interface INetFwPolicy2 : IDispatchSlots
{
    int GetCurrentProfileTypes();

    short GetFirewallEnabled(int profile);

    void PutFirewallEnabled(int profile, short enabled);

    Variant GetExcludedInterfaces(int profile);

    void PutExcludedInterfaces(int profile, Variant interfaces);

    short GetBlockAllInboundTraffic(int profile);

    void PutBlockAllInboundTraffic(int profile, short block);

    short GetNotificationsDisabled(int profile);

    void PutNotificationsDisabled(int profile, short disabled);

    short GetUnicastResponsesToMulticastBroadcastDisabled(int profile);

    void PutUnicastResponsesToMulticastBroadcastDisabled(int profile, short disabled);

    INetFwRules GetRules();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
internal partial interface INetFwRules : IDispatchSlots
{
    int GetCount();

    void Add(INetFwRule rule);

    void Remove(string name);

    INetFwRule Item(string name);

    nint GetNewEnum();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
internal partial interface INetFwRule : IDispatchSlots
{
    string? GetName();

    void PutName(string? name);

    string? GetDescription();

    void PutDescription(string? description);

    string? GetApplicationName();

    void PutApplicationName(string? applicationName);

    string? GetServiceName();

    void PutServiceName(string? serviceName);

    int GetProtocol();

    void PutProtocol(int protocol);

    string? GetLocalPorts();

    void PutLocalPorts(string? ports);

    string? GetRemotePorts();

    void PutRemotePorts(string? ports);

    string? GetLocalAddresses();

    void PutLocalAddresses(string? addresses);

    string? GetRemoteAddresses();

    void PutRemoteAddresses(string? addresses);

    string? GetIcmpTypesAndCodes();

    void PutIcmpTypesAndCodes(string? codes);

    int GetDirection();

    void PutDirection(int direction);

    Variant GetInterfaces();

    void PutInterfaces(Variant interfaces);

    string? GetInterfaceTypes();

    void PutInterfaceTypes(string? types);

    short GetEnabled();

    void PutEnabled(short enabled);

    string? GetGrouping();

    void PutGrouping(string? grouping);

    int GetProfiles();

    void PutProfiles(int profiles);

    short GetEdgeTraversal();

    void PutEdgeTraversal(short edgeTraversal);

    int GetAction();

    void PutAction(int action);
}

[GeneratedComInterface]
[Guid("00020404-0000-0000-C000-000000000046")]
internal partial interface IEnumVariant
{
    [PreserveSig]
    int Next(uint count, out Variant item, out uint fetched);
}
