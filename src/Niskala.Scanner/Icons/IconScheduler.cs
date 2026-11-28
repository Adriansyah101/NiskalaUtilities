using Niskala.Core.Models;

namespace Niskala.Scanner.Icons;

public sealed class IconScheduler : IDisposable
{
    public const int DefaultConcurrency = 3;

    private readonly Func<InstalledApp, CancellationToken, Task<string?>> _load;
    private readonly int _limit;
    private readonly Lock _lock = new();
    private readonly LinkedList<Job> _waiting = [];
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private int _running;
    private bool _disposed;

    public IconScheduler(Func<InstalledApp, CancellationToken, Task<string?>> load, int concurrency = DefaultConcurrency)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        _load = load;
        _limit = concurrency;
    }

    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count;
            }
        }
    }

    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public Task<string?> Request(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.FromCanceled<string?>(new CancellationToken(canceled: true));
            }

            if (_jobs.TryGetValue(app.Id, out Job? known))
            {
                if (known.Node.List is not null)
                {
                    _waiting.Remove(known.Node);
                    _waiting.AddFirst(known.Node);
                }

                return known.Completion.Task;
            }

            var job = new Job(app);
            _jobs[app.Id] = job;
            _waiting.AddFirst(job.Node);
            Pump();
            return job.Completion.Task;
        }
    }

    public bool Withdraw(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out Job? job) || job.Node.List is null)
            {
                return false;
            }

            _waiting.Remove(job.Node);
            _jobs.Remove(id);
            job.Completion.TrySetCanceled();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            foreach (Job job in _waiting)
            {
                job.Completion.TrySetCanceled();
            }

            _waiting.Clear();
            _jobs.Clear();
            if (_running == 0)
            {
                _stop.Dispose();
            }
        }
    }

    private void Pump()
    {
        while (_running < _limit && _waiting.First is { } next)
        {
            _waiting.RemoveFirst();
            _running++;
            Job job = next.Value;
            _ = Task.Run(() => RunAsync(job));
        }
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            string? file = await _load(job.App, _stop.Token).ConfigureAwait(false);
            job.Completion.TrySetResult(file);
        }
        catch (OperationCanceledException)
        {
            job.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            job.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_lock)
            {
                _running--;
                if (_jobs.TryGetValue(job.App.Id, out Job? current) && ReferenceEquals(current, job))
                {
                    _jobs.Remove(job.App.Id);
                }

                if (!_disposed)
                {
                    Pump();
                }
                else if (_running == 0)
                {
                    _stop.Dispose();
                }
            }
        }
    }

    private sealed class Job
    {
        public Job(InstalledApp app)
        {
            App = app;
            Node = new LinkedListNode<Job>(this);
        }

        public InstalledApp App { get; }

        public LinkedListNode<Job> Node { get; }

        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
