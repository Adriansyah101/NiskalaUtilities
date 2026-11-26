using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Niskala.Toolkit.Storage;

public sealed record StorageNode(string Path, string Name, long Bytes, int Files, int Folders, IReadOnlyList<StorageNode> Children)
{
    public bool Inaccessible { get; init; }

    public int Links { get; init; }
}

public sealed record ExtensionGroup(string Extension, int Files, long Bytes);

public sealed record StorageMap(StorageNode Root, IReadOnlyList<ExtensionGroup> Extensions, int Inaccessible, int Links, TimeSpan Elapsed);

public sealed record StorageProgress(int Folders, int Files, long Bytes);

public sealed record StorageMapOptions
{
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    public int ChildrenPerFolder { get; init; } = 64;
}

public static class StorageMapper
{
    private const string NoExtension = "(none)";
    private const int ProgressEvery = 256;

    public static async Task<StorageMap> MapAsync(string root, StorageMapOptions? options = null, IProgress<StorageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        options ??= new StorageMapOptions();
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full.Length == 2 && full[1] == ':')
        {
            full += Path.DirectorySeparatorChar;
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"{full} does not exist.");
        }

        long start = Stopwatch.GetTimestamp();
        var top = new Folder(full, Path.GetFileName(full) is { Length: > 0 } name ? name : full);
        var queue = Channel.CreateUnbounded<Folder>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var totals = new Totals();
        var extensions = new ConcurrentBag<Dictionary<string, (int Files, long Bytes)>>();
        Interlocked.Increment(ref totals.Pending);
        queue.Writer.TryWrite(top);

        Task[] workers = [.. Enumerable.Range(0, Math.Max(1, options.Workers)).Select(_ => Task.Run(async () =>
        {
            var local = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            extensions.Add(local);
            await foreach (Folder folder in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Visit(folder, local, queue.Writer, totals, progress);
                }
                catch (Exception ex)
                {
                    queue.Writer.TryComplete(ex);
                    throw;
                }
                finally
                {
                    if (Interlocked.Decrement(ref totals.Pending) == 0)
                    {
                        queue.Writer.TryComplete();
                    }
                }
            }
        }, cancellationToken))];
        await Task.WhenAll(workers).ConfigureAwait(false);

        StorageNode node = Build(top, options.ChildrenPerFolder);
        ExtensionGroup[] groups = [.. extensions
            .SelectMany(d => d)
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExtensionGroup(g.Key, g.Sum(e => e.Value.Files), g.Sum(e => e.Value.Bytes)))
            .OrderByDescending(g => g.Bytes)];
        return new StorageMap(node, groups, totals.Inaccessible, totals.Links, Stopwatch.GetElapsedTime(start));
    }

    private static void Visit(Folder folder, Dictionary<string, (int Files, long Bytes)> extensions, ChannelWriter<Folder> queue, Totals totals, IProgress<StorageProgress>? progress)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        try
        {
            var entries = new FileSystemEnumerable<Entry>(
                folder.Path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.Attributes, e.Length, e.IsDirectory),
                options);
            foreach (Entry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        folder.Links++;
                        Interlocked.Increment(ref totals.Links);
                        continue;
                    }

                    var child = new Folder(Path.Join(folder.Path, entry.Name), entry.Name);
                    folder.Children.Add(child);
                    Interlocked.Increment(ref totals.Pending);
                    queue.TryWrite(child);
                    continue;
                }

                folder.Files++;
                folder.Bytes += entry.Length;
                string extension = Path.GetExtension(entry.Name) is { Length: > 1 } ext ? ext.ToLowerInvariant() : NoExtension;
                (int count, long size) = extensions.GetValueOrDefault(extension);
                extensions[extension] = (count + 1, size + entry.Length);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            folder.Inaccessible = true;
            Interlocked.Increment(ref totals.Inaccessible);
        }

        int folders = Interlocked.Increment(ref totals.Folders);
        int files = Interlocked.Add(ref totals.Files, folder.Files);
        long bytes = Interlocked.Add(ref totals.Bytes, folder.Bytes);
        if (folders % ProgressEvery == 0)
        {
            progress?.Report(new StorageProgress(folders, files, bytes));
        }
    }

    private static StorageNode Build(Folder top, int childLimit)
    {
        var order = new List<Folder>();
        var stack = new Stack<Folder>([top]);
        while (stack.TryPop(out Folder? folder))
        {
            order.Add(folder);
            foreach (Folder child in folder.Children)
            {
                stack.Push(child);
            }
        }

        var built = new Dictionary<Folder, StorageNode>(order.Count);
        for (int i = order.Count - 1; i >= 0; i--)
        {
            Folder folder = order[i];
            StorageNode[] children = [.. folder.Children.Select(c => built[c]).OrderByDescending(c => c.Bytes)];
            built[folder] = new StorageNode(
                folder.Path,
                folder.Name,
                folder.Bytes + children.Sum(c => c.Bytes),
                folder.Files + children.Sum(c => c.Files),
                folder.Children.Count + children.Sum(c => c.Folders),
                [.. children.Take(childLimit)])
            {
                Inaccessible = folder.Inaccessible,
                Links = folder.Links + children.Sum(c => c.Links),
            };
            foreach (Folder child in folder.Children)
            {
                built.Remove(child);
            }
        }

        return built[top];
    }

    private readonly record struct Entry(string Name, FileAttributes Attributes, long Length, bool IsDirectory);

    private sealed class Folder(string path, string name)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public List<Folder> Children { get; } = [];

        public int Files { get; set; }

        public long Bytes { get; set; }

        public int Links { get; set; }

        public bool Inaccessible { get; set; }
    }

    private sealed class Totals
    {
        public int Pending;
        public int Folders;
        public int Files;
        public long Bytes;
        public int Links;
        public int Inaccessible;
    }
}
