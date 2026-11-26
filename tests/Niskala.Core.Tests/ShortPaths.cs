using System.Runtime.InteropServices;

namespace Niskala.Core.Tests;

internal static partial class ShortPaths
{
    private const int Capacity = 1024;

    public static string ToShort(string path) => Convert(path, GetShortPathName);

    public static string ToLong(string path) => Convert(path, GetLongPathName);

    private static string Convert(string path, Func<string, char[], uint, uint> api)
    {
        char[] buffer = new char[Capacity];
        uint length = api(path, buffer, Capacity);
        return length is > 0 and < Capacity ? new string(buffer, 0, (int)length) : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string path, [Out] char[] buffer, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string path, [Out] char[] buffer, uint length);
}
