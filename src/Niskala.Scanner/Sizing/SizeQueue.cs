namespace Niskala.Scanner.Sizing;

public sealed class SizeQueue : IDisposable
{
    private readonly Func<string, VolumeInfo> _probe;
    private readonly Func<SizeRequest, DiskKind, CancellationToken, FolderSize> _measure;
    private readonly FolderSizeCache? _cache;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Volume> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Volume Volume, LinkedListNode<SizeRequest> Node)> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;

    public SizeQueue(Func<string, VolumeInfo> probe, Func<SizeRequest, DiskKind, CancellationToken, FolderSize> measure, FolderSizeCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(measure);
        _probe = probe;
        _cache = cache;
        _measure = cache is null ? measure : (request, kind, token) => cache.Measure(request, kind, token, measure);
        _token = _stop.Token;
    }

    public event Action<FolderSize>? Measured;

    public static SizeQueue Create(DiskKindProbe probe, FolderSizeCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return new SizeQueue(probe.Probe, FolderSizeService.Measure, cache);
    }

    public void Enqueue(SizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        VolumeInfo info = _probe(request.Path);
        if (!_token.IsCancellationRequested && _cache?.TryGet(request, info.Kind) is { } cached)
        {
            lock (_lock)
            {
                if (_pending.ContainsKey(request.Id))
                {
                    return;
                }
            }

            Measured?.Invoke(cached);
            return;
        }

        lock (_lock)
        {
            if (_token.IsCancellationRequested || _pending.ContainsKey(request.Id))
            {
                return;
            }

            if (!_volumes.TryGetValue(info.Volume, out Volume? volume))
            {
                volume = new Volume(info.Kind);
                _volumes.Add(info.Volume, volume);
            }

            _pending.Add(request.Id, (volume, volume.Queue.AddLast(request)));
            if (volume.Workers < DiskKindProbe.WorkersFor(volume.Kind))
            {
                volume.Workers++;
                _ = Task.Factory.StartNew(() => Work(volume), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }
    }

    public void Prioritize(string id)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(id, out (Volume Volume, LinkedListNode<SizeRequest> Node) entry))
            {
                entry.Volume.Queue.Remove(entry.Node);
                entry.Volume.Queue.AddFirst(entry.Node);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_token.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
            _pending.Clear();
            foreach (Volume volume in _volumes.Values)
            {
                volume.Queue.Clear();
            }
        }

        _cache?.Save();
        _stop.Dispose();
    }

    private void Work(Volume volume)
    {
        while (true)
        {
            SizeRequest request;
            lock (_lock)
            {
                if (_token.IsCancellationRequested || volume.Queue.First is not { } first)
                {
                    volume.Workers--;
                    if (!_token.IsCancellationRequested && _pending.Count == 0 && _volumes.Values.All(v => v.Workers == 0))
                    {
                        _ = Task.Run(() => _cache?.Save());
                    }

                    return;
                }

                request = first.Value;
                volume.Queue.RemoveFirst();
                _pending.Remove(request.Id);
            }

            FolderSize result;
            try
            {
                result = _measure(request, volume.Kind, _token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Measured?.Invoke(result);
        }
    }

    private sealed class Volume(DiskKind kind)
    {
        public DiskKind Kind { get; } = kind;

        public LinkedList<SizeRequest> Queue { get; } = [];

        public int Workers { get; set; }
    }
}
