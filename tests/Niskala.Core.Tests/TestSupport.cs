using System.Diagnostics;
using Niskala.Core.Safety;

namespace Niskala.Core.Tests;

internal sealed class FakeResolver : IPathResolver
{
    private readonly List<(string From, string To)> _links = [];

    public bool Exists { get; init; } = true;

    public FakeResolver Link(string from, string to)
    {
        _links.Add((from, to));
        return this;
    }

    public PathResolution Resolve(string normalizedPath)
    {
        foreach ((string from, string to) in _links)
        {
            if (PathSyntax.IsSameOrUnder(normalizedPath, from, out bool same))
            {
                return same
                    ? new PathResolution(ResolutionStatus.Resolved, normalizedPath, Exists, true, true, 0)
                    : new PathResolution(ResolutionStatus.Resolved, to + normalizedPath[from.Length..], Exists, false, false, 0);
            }
        }

        return new PathResolution(ResolutionStatus.Resolved, normalizedPath, Exists, false, false, 0);
    }
}

internal sealed class TempDirectory : IDisposable
{
    private readonly List<string> _junctions = [];

    public TempDirectory()
    {
        Root = Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string CreateDirectory(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string CreateJunction(string relative, string target)
    {
        string link = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);

        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new InvalidOperationException($"mklink /J failed: {process.StandardError.ReadToEnd()}");
        }

        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string junction in _junctions)
        {
            Directory.Delete(junction, recursive: false);
        }

        Directory.Delete(Root, recursive: true);
    }
}
