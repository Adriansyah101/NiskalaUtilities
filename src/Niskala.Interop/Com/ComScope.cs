using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Niskala.Interop.Com;

internal readonly ref struct ComScope
{
    private readonly bool _initialized;

    private ComScope(bool initialized) => _initialized = initialized;

    public static ComScope Enter()
    {
        HRESULT result = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        return new ComScope(result.Succeeded);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            PInvoke.CoUninitialize();
        }
    }
}
