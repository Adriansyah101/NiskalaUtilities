using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Niskala.Scanner.Sizing;

public sealed record SizeRequest(string Id, string Path);

public sealed record FolderSize(
    string Id,
    string Path,
    bool Exists,
    bool IsLink,
    long Bytes,
    long Files,
    long Directories,
    DiskKind Disk,
    TimeSpan Elapsed,
    string? Error = null);

public sealed class FolderSizeService(DiskKindProbe probe)
{
    private const int CancellationCheckInterval = 1024;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public FolderSize Measure(SizeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Measure(request, probe.Probe(request.Path).Kind, cancellationToken);
    }

    public async IAsyncEnumerable<FolderSize> MeasureAllAsync(
        IEnumerable<SizeRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        Channel<FolderSize> results = Channel.CreateUnbounded<FolderSize>(new UnboundedChannelOptions { SingleReader = true });
        var workers = new List<Task>();

        foreach (IGrouping<VolumeInfo, SizeRequest> volume in requests.GroupBy(request => probe.Probe(request.Path)))
        {
            Channel<SizeRequest> queue = Channel.CreateUnbounded<SizeRequest>();
            foreach (SizeRequest request in volume)
            {
                queue.Writer.TryWrite(request);
            }

            queue.Writer.Complete();
            DiskKind kind = volume.Key.Kind;
            for (int i = 0; i < DiskKindProbe.WorkersFor(kind); i++)
            {
                workers.Add(Task.Run(
                    async () =>
                    {
                        await foreach (SizeRequest request in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await results.Writer.WriteAsync(Measure(request, kind, cancellationToken), cancellationToken).ConfigureAwait(false);
                        }
                    },
                    cancellationToken));
            }
        }

        _ = Task.WhenAll(workers).ContinueWith(
            done => results.Writer.TryComplete(done.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (FolderSize result in results.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    public static FolderSize Measure(SizeRequest request, DiskKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long start = Stopwatch.GetTimestamp();
        FolderSize Result(bool exists, bool isLink, long bytes = 0, long files = 0, long directories = 0, string? error = null) =>
            new(request.Id, request.Path, exists, isLink, bytes, files, directories, kind, Stopwatch.GetElapsedTime(start), error);

        try
        {
            if (!Directory.Exists(request.Path))
            {
                return Result(exists: false, isLink: false);
            }

            if ((File.GetAttributes(request.Path) & FileAttributes.ReparsePoint) != 0)
            {
                return Result(exists: true, isLink: true);
            }

            long bytes = 0, files = 0, directories = 0;
            var entries = new FileSystemEnumerable<long>(
                request.Path,
                static (ref FileSystemEntry entry) => entry.IsDirectory ? -1 : entry.Length,
                Options)
            {
                ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            foreach (long length in entries)
            {
                if (length < 0)
                {
                    directories++;
                }
                else
                {
                    files++;
                    bytes += length;
                }

                if ((files + directories) % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return Result(exists: true, isLink: false, bytes, files, directories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result(exists: true, isLink: false, error: ex.Message);
        }
    }
}
