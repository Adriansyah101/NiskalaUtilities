using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety;
using Niskala.Interop.FileSystem;

namespace Niskala.Toolkit.Duplicates;

public enum KeepRule
{
    Oldest,
    Newest,
}

public enum DuplicateStage
{
    Listing,
    PartialHash,
    FullHash,
}

public sealed record DuplicateFile(string Path, long Bytes, DateTime LastWriteUtc, DateTime CreatedUtc)
{
    public string? Protection { get; init; }

    public bool IsProtected => Protection is not null;
}

public sealed record DuplicateGroup(string Hash, long Bytes, IReadOnlyList<DuplicateFile> Files)
{
    public long Reclaimable => Bytes * (Files.Count - 1);
}

public sealed record DuplicateReport(IReadOnlyList<DuplicateGroup> Groups, int Scanned, int SameSize, int SamePartial, int Unreadable, int HardLinks, TimeSpan Elapsed)
{
    public long Reclaimable => Groups.Sum(g => g.Reclaimable);
}

public sealed record DuplicateOptions
{
    public long MinimumBytes { get; init; } = 1;

    public bool IncludeHidden { get; init; }

    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
}

public sealed record DuplicateProgress(DuplicateStage Stage, int Done, int Total);

public sealed class DuplicateFinder(PathGuard guard)
{
    public const int SampleBytes = 4096;

    private const int ProgressEvery = 128;

    public async Task<DuplicateReport> FindAsync(
        IReadOnlyList<string> roots,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        options ??= new DuplicateOptions();
        long start = Stopwatch.GetTimestamp();
        List<DuplicateFile> files = List(roots, options, cancellationToken);
        progress?.Report(new DuplicateProgress(DuplicateStage.Listing, files.Count, files.Count));

        List<DuplicateFile> sameSize = [.. files.GroupBy(f => f.Bytes).Where(g => g.Count() > 1).SelectMany(g => g)];
        var unreadable = new Counter();
        var links = new Counter();
        ConcurrentDictionary<string, (string Key, bool Final)> partial = await HashAsync(sameSize, Sample, DuplicateStage.PartialHash, options, progress, unreadable, cancellationToken).ConfigureAwait(false);
        List<DuplicateFile> unique = DropHardLinks(sameSize.Where(f => partial.ContainsKey(f.Path)), links);

        List<DuplicateFile> samePartial = [.. unique
            .GroupBy(f => (f.Bytes, partial[f.Path].Key))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)];
        ConcurrentDictionary<string, (string Key, bool Final)> full = await HashAsync(
            [.. samePartial.Where(f => !partial[f.Path].Final)], Full, DuplicateStage.FullHash, options, progress, unreadable, cancellationToken).ConfigureAwait(false);

        DuplicateGroup[] groups = [.. samePartial
            .Select(f => (File: f, Hash: partial[f.Path].Final ? partial[f.Path].Key : full.TryGetValue(f.Path, out var h) ? h.Key : null))
            .Where(f => f.Hash is not null)
            .GroupBy(f => (f.File.Bytes, f.Hash!))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.Key.Item2, g.Key.Bytes, [.. g.Select(f => Guarded(f.File)).OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)]))
            .OrderByDescending(g => g.Reclaimable)];
        return new DuplicateReport(groups, files.Count, sameSize.Count, samePartial.Count, unreadable.Value, links.Value, Stopwatch.GetElapsedTime(start));
    }

    public static string? FullHash(string path)
    {
        try
        {
            using FileStream stream = Open(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<DuplicateFile> List(IReadOnlyList<string> roots, DuplicateOptions options, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<DuplicateFile>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
            ReturnSpecialDirectories = false,
        };
        foreach (string root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var entries = new FileSystemEnumerable<DuplicateFile>(
                root,
                (ref FileSystemEntry e) => new DuplicateFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime, e.CreationTimeUtc.UtcDateTime),
                enumeration)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.Length >= options.MinimumBytes && e.Length > 0,
            };
            foreach (DuplicateFile file in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(file.Path))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private static async Task<ConcurrentDictionary<string, (string Key, bool Final)>> HashAsync(
        IReadOnlyList<DuplicateFile> files,
        Func<DuplicateFile, (string Key, bool Final)> hash,
        DuplicateStage stage,
        DuplicateOptions options,
        IProgress<DuplicateProgress>? progress,
        Counter unreadable,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellationToken }, (file, _) =>
        {
            try
            {
                results[file.Path] = hash(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add();
            }

            int count = Interlocked.Increment(ref done);
            if (count % ProgressEvery == 0 || count == files.Count)
            {
                progress?.Report(new DuplicateProgress(stage, count, files.Count));
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return results;
    }

    private static (string Key, bool Final) Sample(DuplicateFile file)
    {
        using FileStream stream = Open(file.Path);
        long length = stream.Length;
        if (length <= 2 * SampleBytes)
        {
            return (Convert.ToHexStringLower(SHA256.HashData(stream)), true);
        }

        byte[] buffer = new byte[2 * SampleBytes];
        stream.ReadExactly(buffer, 0, SampleBytes);
        stream.Seek(-SampleBytes, SeekOrigin.End);
        stream.ReadExactly(buffer, SampleBytes, SampleBytes);
        return (Convert.ToHexStringLower(SHA256.HashData(buffer)), false);
    }

    private static (string Key, bool Final) Full(DuplicateFile file)
    {
        using FileStream stream = Open(file.Path);
        return (Convert.ToHexStringLower(SHA256.HashData(stream)), true);
    }

    private static List<DuplicateFile> DropHardLinks(IEnumerable<DuplicateFile> files, Counter links)
    {
        var identities = new HashSet<(uint, ulong)>();
        var unique = new List<DuplicateFile>();
        foreach (DuplicateFile file in files)
        {
            if (Identity(file.Path) is { } identity && !identities.Add(identity))
            {
                links.Add();
                continue;
            }

            unique.Add(file);
        }

        return unique;
    }

    private static (uint, ulong)? Identity(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileLinkNative.TryGet(handle, out FileLinkInfo info) == 0 ? (info.VolumeSerial, info.FileIndex) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream Open(string path) =>
        new(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan });

    private DuplicateFile Guarded(DuplicateFile file) =>
        guard.Check(file.Path) is { IsAllowed: false } decision ? file with { Protection = decision.ToString() } : file;

    private sealed class Counter
    {
        private int _value;

        public int Value => _value;

        public void Add() => Interlocked.Increment(ref _value);
    }
}

public static class DuplicateSelection
{
    public static DuplicateFile Keeper(DuplicateGroup group, KeepRule rule)
    {
        ArgumentNullException.ThrowIfNull(group);
        IOrderedEnumerable<DuplicateFile> ordered = rule == KeepRule.Oldest
            ? group.Files.OrderBy(f => f.LastWriteUtc).ThenBy(f => f.CreatedUtc)
            : group.Files.OrderByDescending(f => f.LastWriteUtc).ThenByDescending(f => f.CreatedUtc);
        return ordered.ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
    }

    public static IReadOnlyList<DuplicateFile> Removable(DuplicateGroup group, KeepRule rule)
    {
        DuplicateFile keeper = Keeper(group, rule);
        return [.. group.Files.Where(f => !ReferenceEquals(f, keeper) && !f.IsProtected)];
    }
}
