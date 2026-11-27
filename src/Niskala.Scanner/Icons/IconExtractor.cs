using System.Collections.Concurrent;
using Niskala.Interop.Icons;

namespace Niskala.Scanner.Icons;

public sealed class IconExtractor : IDisposable
{
    private readonly BlockingCollection<Request> _queue = [];
    private readonly Thread _thread;

    public IconExtractor()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Niskala icon extractor" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<IconPixels?> ExtractAsync(IconSource source, int size, CancellationToken cancellationToken = default) =>
        ExtractAsync(source, size, shellFallback: true, cancellationToken);

    public Task<IconPixels?> ExtractAsync(IconSource source, int size, bool shellFallback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != IconSourceKind.Resource)
        {
            throw new ArgumentException("Only icon resources are extracted.", nameof(source));
        }

        var request = new Request(source, size, shellFallback, cancellationToken, new TaskCompletionSource<IconPixels?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Add(request, cancellationToken);
        return request.Completion.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (Request request in _queue.GetConsumingEnumerable())
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                continue;
            }

            try
            {
                IconSource source = request.Source;
                IconPixels? pixels = IconNative.TryExtractIcon(source.Path, source.Index, request.Size)
                    ?? (request.ShellFallback && source.Index == 0 ? IconNative.TryGetShellIcon(source.Path, request.Size) : null);
                request.Completion.TrySetResult(pixels);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
        }
    }

    private sealed record Request(IconSource Source, int Size, bool ShellFallback, CancellationToken CancellationToken, TaskCompletionSource<IconPixels?> Completion);
}
