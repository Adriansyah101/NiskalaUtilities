using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.ApplicationInstallationAndServicing;

namespace Niskala.Interop.Msi;

[Flags]
public enum MsiContext
{
    None = 0,
    UserManaged = 1,
    UserUnmanaged = 2,
    Machine = 4,
    All = UserManaged | UserUnmanaged | Machine,
}

public readonly record struct MsiProductRef(string ProductCode, MsiContext Context);

public readonly record struct MsiClient(string ProductCode, MsiContext Context);

public enum MsiComponentState
{
    Unknown,
    Local,
    Source,
    Absent,
    Broken,
    Other,
}

public enum MsiProductState
{
    NotRegistered,
    Installed,
    Advertised,
    OtherUser,
    Other,
}

public static class MsiNative
{
    public const int ErrorSuccess = 0;
    public const int ErrorMoreData = 234;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorUnknownProduct = 1605;
    public const int ErrorUnknownProperty = 1608;

    private const int GuidBufferLength = 39;

    public static unsafe int TryEnumerateProducts(out List<MsiProductRef> products)
    {
        products = [];
        char* code = stackalloc char[GuidBufferLength];
        for (uint index = 0; ; index++)
        {
            MSIINSTALLCONTEXT context;
            uint rc = PInvoke.MsiEnumProductsEx(
                default(PCWSTR), default(PCWSTR), (uint)MSIINSTALLCONTEXT.MSIINSTALLCONTEXT_ALL, index, code, &context, default(PWSTR), null);
            if (rc == ErrorNoMoreItems)
            {
                return ErrorSuccess;
            }

            if (rc != ErrorSuccess)
            {
                return (int)rc;
            }

            products.Add(new MsiProductRef(new string(code), (MsiContext)(int)context));
        }
    }

    public static unsafe int TryEnumerateComponents(out List<string> components)
    {
        components = [];
        char* code = stackalloc char[GuidBufferLength];
        for (uint index = 0; ; index++)
        {
            uint rc = PInvoke.MsiEnumComponentsEx(
                default(PCWSTR), (uint)MSIINSTALLCONTEXT.MSIINSTALLCONTEXT_ALL, index, code, null, default(PWSTR), null);
            if (rc == ErrorNoMoreItems)
            {
                return ErrorSuccess;
            }

            if (rc != ErrorSuccess)
            {
                return (int)rc;
            }

            components.Add(new string(code));
        }
    }

    public static unsafe int TryEnumerateClients(string componentCode, out List<MsiClient> clients)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentCode);
        clients = [];
        char* product = stackalloc char[GuidBufferLength];
        fixed (char* component = componentCode)
        {
            for (uint index = 0; ; index++)
            {
                MSIINSTALLCONTEXT context;
                uint rc = PInvoke.MsiEnumClientsEx(
                    component, default(PCWSTR), MSIINSTALLCONTEXT.MSIINSTALLCONTEXT_ALL, index, product, &context, default(PWSTR), null);
                if (rc == ErrorNoMoreItems)
                {
                    return ErrorSuccess;
                }

                if (rc != ErrorSuccess)
                {
                    return (int)rc;
                }

                clients.Add(new MsiClient(new string(product), (MsiContext)(int)context));
            }
        }
    }

    public static unsafe MsiComponentState GetComponentPath(string productCode, string componentCode, MsiContext context, out string? path)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        ArgumentException.ThrowIfNullOrEmpty(componentCode);
        path = null;

        uint capacity = 260;
        while (true)
        {
            char[] buffer = new char[capacity];
            uint length = (uint)buffer.Length;
            INSTALLSTATE state;
            fixed (char* p = productCode)
            fixed (char* c = componentCode)
            fixed (char* outBuffer = buffer)
            {
                state = PInvoke.MsiGetComponentPathEx(p, c, default(PCWSTR), (MSIINSTALLCONTEXT)(int)context, outBuffer, &length);
            }

            switch (state)
            {
                case INSTALLSTATE.INSTALLSTATE_MOREDATA when length < short.MaxValue:
                    capacity = length + 1;
                    continue;
                case INSTALLSTATE.INSTALLSTATE_LOCAL:
                    path = new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length));
                    return MsiComponentState.Local;
                case INSTALLSTATE.INSTALLSTATE_SOURCE:
                    path = new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length));
                    return MsiComponentState.Source;
                case INSTALLSTATE.INSTALLSTATE_ABSENT:
                    return MsiComponentState.Absent;
                case INSTALLSTATE.INSTALLSTATE_BROKEN:
                    return MsiComponentState.Broken;
                case INSTALLSTATE.INSTALLSTATE_UNKNOWN:
                    return MsiComponentState.Unknown;
                default:
                    return MsiComponentState.Other;
            }
        }
    }

    public static unsafe int TryGetProductInfo(string productCode, MsiContext context, string property, out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        ArgumentException.ThrowIfNullOrEmpty(property);
        value = null;

        uint capacity = 260;
        while (true)
        {
            char[] buffer = new char[capacity];
            uint length = (uint)buffer.Length;
            uint rc;
            fixed (char* p = productCode)
            fixed (char* name = property)
            fixed (char* outBuffer = buffer)
            {
                rc = PInvoke.MsiGetProductInfoEx(p, default(PCWSTR), (MSIINSTALLCONTEXT)(int)context, name, outBuffer, &length);
            }

            if (rc == ErrorMoreData && length < short.MaxValue)
            {
                capacity = length + 1;
                continue;
            }

            if (rc != ErrorSuccess)
            {
                return (int)rc;
            }

            value = new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length));
            return ErrorSuccess;
        }
    }

    public static unsafe MsiProductState QueryProductState(string productCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        fixed (char* code = productCode)
        {
            return PInvoke.MsiQueryProductState(code) switch
            {
                INSTALLSTATE.INSTALLSTATE_DEFAULT => MsiProductState.Installed,
                INSTALLSTATE.INSTALLSTATE_ADVERTISED => MsiProductState.Advertised,
                INSTALLSTATE.INSTALLSTATE_ABSENT => MsiProductState.OtherUser,
                INSTALLSTATE.INSTALLSTATE_UNKNOWN or INSTALLSTATE.INSTALLSTATE_INVALIDARG => MsiProductState.NotRegistered,
                _ => MsiProductState.Other,
            };
        }
    }
}
