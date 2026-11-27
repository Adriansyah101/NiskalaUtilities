using System.Globalization;

[assembly: AssemblyFixture(typeof(Niskala.Updater.Tests.TestRoots))]

namespace Niskala.Updater.Tests;

public sealed class TestRoots : IDisposable
{
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"niskala-tests-{Environment.ProcessId}"));

    public static string NewFolder() => Directory.CreateDirectory(Path.Combine(Folder, Guid.NewGuid().ToString("N"))).FullName;

    public static string Repository()
    {
        for (string? folder = AppContext.BaseDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (File.Exists(Path.Combine(folder, "Niskala.sln")))
            {
                return folder;
            }
        }

        throw new DirectoryNotFoundException("Niskala.sln was not found above the test folder.");
    }

    public void Dispose()
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
