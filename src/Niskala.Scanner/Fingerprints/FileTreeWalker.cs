using System.IO.Enumeration;

namespace Niskala.Scanner.Fingerprints;

internal static class FileTreeWalker
{
    private const int CancellationCheckInterval = 1024;

    private static readonly string[] SharedPrograms =
        ["vc_redist", "vcredist", "dxsetup", "dxwebsetup", "UnityCrashHandler", "CrashReportClient", "UEPrereqSetup", "UE4PrereqSetup"];

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static (FileTreeSnapshot Snapshot, IReadOnlyList<string> Executables) Walk(string root, int maxEntries, CancellationToken cancellationToken)
    {
        var entries = new List<FileTreeEntry>();
        var executables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        int directories = 0;
        long bytes = 0;

        var enumeration = new FileSystemEnumerable<FileTreeEntry>(root, Transform, Options)
        {
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        int seen = 0;
        foreach (FileTreeEntry entry in enumeration)
        {
            if (++seen % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (entry.IsDirectory)
            {
                directories++;
            }
            else
            {
                files++;
                bytes += entry.Size;
                string name = Path.GetFileName(entry.RelativePath);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !SharedPrograms.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    executables.Add(name);
                }
            }

            if (entries.Count < maxEntries)
            {
                entries.Add(entry);
            }
        }

        return (new FileTreeSnapshot(root, entries, files, directories, bytes, entries.Count < files + directories), [.. executables]);
    }

    private static FileTreeEntry Transform(ref FileSystemEntry entry)
    {
        ReadOnlySpan<char> below = entry.Directory[entry.RootDirectory.Length..].TrimStart(['\\', '/']);
        return new FileTreeEntry(
            Path.Join(below, entry.FileName),
            entry.IsDirectory,
            (entry.Attributes & FileAttributes.ReparsePoint) != 0,
            entry.IsDirectory ? 0 : entry.Length,
            entry.LastWriteTimeUtc.UtcDateTime);
    }
}
