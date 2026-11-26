using System.Globalization;
using Hives = Microsoft.Win32.Registry;

[assembly: AssemblyFixture(typeof(Niskala.Scanner.Tests.TestRoots))]

namespace Niskala.Scanner.Tests;

public sealed class TestRoots : IDisposable
{
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"niskala-tests-{Environment.ProcessId}"));

    public static string Key { get; } = string.Create(CultureInfo.InvariantCulture, $@"Software\NiskalaTests-{Environment.ProcessId}");

    public void Dispose()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }

        Hives.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
    }
}
