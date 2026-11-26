using Windows.Win32;
using Windows.Win32.Foundation;

namespace Niskala.Interop.Com;

internal static class BstrText
{
    public static unsafe string Take(BSTR value)
    {
        try
        {
            return value.Value is null ? string.Empty : new string(value.Value);
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }
}
