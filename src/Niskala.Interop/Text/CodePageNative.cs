using Windows.Win32;

namespace Niskala.Interop.Text;

public static class CodePageNative
{
    public static int OemCodePage => (int)PInvoke.GetOEMCP();
}
