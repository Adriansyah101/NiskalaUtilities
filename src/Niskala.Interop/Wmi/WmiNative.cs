using System.Runtime.InteropServices;
using Niskala.Interop.Com;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.System.Wmi;

namespace Niskala.Interop.Wmi;

public static class WmiNative
{
    private const uint AuthenticationWinNt = 10;
    private const uint AuthorizationNone = 0;
    private const int Infinite = -1;

    public static unsafe int TryInvoke(
        string wmiNamespace,
        string className,
        string method,
        IReadOnlyList<KeyValuePair<string, object>> inputs,
        IReadOnlyList<string> outputNames,
        out Dictionary<string, object?> outputs)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputNames);
        outputs = [];
        using ComScope com = ComScope.Enter();
        IWbemServices* services = null;
        IWbemClassObject* definition = null;
        IWbemClassObject* signature = null;
        IWbemClassObject* arguments = null;
        IWbemClassObject* results = null;
        using SysFreeStringSafeHandle classText = PInvoke.SysAllocString(className);
        using SysFreeStringSafeHandle methodText = PInvoke.SysAllocString(method);
        try
        {
            int connected = TryConnect(wmiNamespace, out services);
            if (connected != 0)
            {
                return connected;
            }

            services->GetObject(classText, 0, null, &definition, null);
            fixed (char* name = method)
            {
                definition->GetMethod(name, 0, &signature, null);
            }

            if (signature is not null)
            {
                signature->SpawnInstance(0, &arguments);
                foreach ((string name, object value) in inputs)
                {
                    VARIANT variant = ToVariant(value);
                    try
                    {
                        fixed (char* text = name)
                        {
                            arguments->Put(text, 0, &variant, 0);
                        }
                    }
                    finally
                    {
                        PInvoke.VariantClear(&variant);
                    }
                }
            }

            services->ExecMethod(classText, methodText, 0, null, arguments, &results, null);
            foreach (string name in outputNames)
            {
                outputs[name] = results is null ? null : Read(results, name);
            }

            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(results);
            Release(arguments);
            Release(signature);
            Release(definition);
            Release(services);
        }
    }

    public static unsafe int TryQuery(string wmiNamespace, string query, IReadOnlyList<string> properties, out List<Dictionary<string, object?>> rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(properties);
        rows = [];
        using ComScope com = ComScope.Enter();
        IWbemServices* services = null;
        IEnumWbemClassObject* enumerator = null;
        using SysFreeStringSafeHandle language = PInvoke.SysAllocString("WQL");
        using SysFreeStringSafeHandle text = PInvoke.SysAllocString(query);
        try
        {
            int connected = TryConnect(wmiNamespace, out services);
            if (connected != 0)
            {
                return connected;
            }

            services->ExecQuery(language, text, WBEM_GENERIC_FLAG_TYPE.WBEM_FLAG_FORWARD_ONLY | WBEM_GENERIC_FLAG_TYPE.WBEM_FLAG_RETURN_IMMEDIATELY, null, &enumerator);
            SetBlanket((IUnknown*)enumerator);
            while (true)
            {
                IWbemClassObject* row = null;
                uint returned = 0;
                HRESULT next = enumerator->Next(Infinite, 1, &row, &returned);
                if (next.Failed)
                {
                    return next.Value;
                }

                if (returned == 0)
                {
                    return 0;
                }

                try
                {
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (string property in properties)
                    {
                        values[property] = Read(row, property);
                    }

                    rows.Add(values);
                }
                finally
                {
                    row->Release();
                }
            }
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(enumerator);
            Release(services);
        }
    }

    private static unsafe int TryConnect(string wmiNamespace, out IWbemServices* services)
    {
        services = null;
        IWbemLocator* locator = null;
        using SysFreeStringSafeHandle path = PInvoke.SysAllocString(wmiNamespace);
        try
        {
            HRESULT created = WbemLocator.CreateInstance(out locator);
            if (created.Failed)
            {
                return created.Value;
            }

            IWbemServices* connected;
            locator->ConnectServer(new BSTR((char*)path.DangerousGetHandle()), default(BSTR), default(BSTR), default(BSTR), 0, default(BSTR), null, &connected);
            services = connected;

            SetBlanket((IUnknown*)services);
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        finally
        {
            Release(locator);
        }
    }

    private static unsafe void SetBlanket(IUnknown* proxy) =>
        PInvoke.CoSetProxyBlanket(
            proxy,
            AuthenticationWinNt,
            AuthorizationNone,
            default,
            RPC_C_AUTHN_LEVEL.RPC_C_AUTHN_LEVEL_CALL,
            RPC_C_IMP_LEVEL.RPC_C_IMP_LEVEL_IMPERSONATE,
            null,
            EOLE_AUTHENTICATION_CAPABILITIES.EOAC_NONE).ThrowOnFailure();

    private static unsafe VARIANT ToVariant(object value)
    {
        var variant = default(VARIANT);
        switch (value)
        {
            case int number:
                variant.Anonymous.Anonymous.vt = VARENUM.VT_I4;
                variant.Anonymous.Anonymous.Anonymous.lVal = number;
                break;
            case string text:
                variant.Anonymous.Anonymous.vt = VARENUM.VT_BSTR;
                variant.Anonymous.Anonymous.Anonymous.bstrVal = new BSTR((char*)Marshal.StringToBSTR(text));
                break;
            case bool flag:
                variant.Anonymous.Anonymous.vt = VARENUM.VT_BOOL;
                variant.Anonymous.Anonymous.Anonymous.boolVal = flag ? VARIANT_BOOL.VARIANT_TRUE : VARIANT_BOOL.VARIANT_FALSE;
                break;
            default:
                throw new ArgumentException($"WMI values must be int, string or bool, not {value.GetType().Name}.", nameof(value));
        }

        return variant;
    }

    private static unsafe object? Read(IWbemClassObject* source, string name)
    {
        VARIANT variant;
        fixed (char* text = name)
        {
            source->Get(text, 0, &variant, null, null);
        }

        try
        {
            ref var inner = ref variant.Anonymous.Anonymous;
            return inner.vt switch
            {
                VARENUM.VT_I4 => inner.Anonymous.lVal,
                VARENUM.VT_I2 => (int)inner.Anonymous.iVal,
                VARENUM.VT_UI1 => (int)inner.Anonymous.bVal,
                VARENUM.VT_BOOL => inner.Anonymous.boolVal != VARIANT_BOOL.VARIANT_FALSE,
                VARENUM.VT_BSTR => inner.Anonymous.bstrVal.Value is null ? null : new string(inner.Anonymous.bstrVal.Value),
                _ => null,
            };
        }
        finally
        {
            PInvoke.VariantClear(&variant);
        }
    }

    private static unsafe void Release<T>(T* pointer)
        where T : unmanaged
    {
        if (pointer is not null)
        {
            ((IUnknown*)pointer)->Release();
        }
    }
}
