using System.ComponentModel;

namespace Niskala.Optimizer.Drives;

public enum DriveAssessment
{
    Tidy,
    SlightlyFragmented,
    HeavilyFragmented,
}

public static class DriveAssessor
{
    public static DriveAssessment Assess(DriveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.FragmentedPercent >= StrategyGuide.HeavyFragmentedPercent ? DriveAssessment.HeavilyFragmented
            : summary.FragmentedPercent >= StrategyGuide.TidyFragmentedPercent ? DriveAssessment.SlightlyFragmented
            : DriveAssessment.Tidy;
    }
}

public sealed class VolumeMediaCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<char, Entry> _entries = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public static bool SameVolume(DriveVolume cached, DriveVolume current)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(current);
        return cached.Letter == current.Letter
            && cached.SerialNumber == current.SerialNumber
            && cached.TotalClusters == current.TotalClusters
            && string.Equals(cached.FileSystem, current.FileSystem, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetMedia(DriveVolume volume, out MediaProbe probe)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (_gate)
        {
            if (Find(volume) is { Media: { IsCompletedSuccessfully: true } media })
            {
                probe = media.Result;
                return true;
            }
        }

        probe = MediaProbe.Of(volume);
        return false;
    }

    public bool TryGetHealth(DriveVolume volume, out DriveHealth health)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (_gate)
        {
            if (Find(volume) is { Health: { IsCompletedSuccessfully: true } read })
            {
                health = read.Result;
                return true;
            }
        }

        health = DriveHealth.NotRead;
        return false;
    }

    public Task<MediaProbe> Media(DriveVolume volume, Func<DriveVolume, MediaProbe> probe)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            Entry entry = Claim(volume);
            if (entry.Media is not { IsFaulted: false, IsCanceled: false } media)
            {
                media = Start(() => probe(volume));
                entry.Media = media;
            }

            return media;
        }
    }

    public Task<DriveHealth> Health(DriveVolume volume, Func<DriveVolume, DriveHealth> read)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            Entry entry = Claim(volume);
            if (entry.Health is not { IsFaulted: false, IsCanceled: false } health)
            {
                health = Start(() => read(volume));
                entry.Health = health;
            }

            return health;
        }
    }

    public int Prune(IEnumerable<DriveVolume> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        Dictionary<char, DriveVolume> now = present.ToDictionary(v => v.Letter);
        lock (_gate)
        {
            char[] gone = [.. _entries.Where(e => !now.TryGetValue(e.Key, out DriveVolume? volume) || !SameVolume(e.Value.Volume, volume)).Select(e => e.Key)];
            foreach (char letter in gone)
            {
                _entries.Remove(letter);
            }

            return gone.Length;
        }
    }

    public void ForgetHealth()
    {
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Health is { IsCompleted: true })
                {
                    entry.Health = null;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static Task<T> Start<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);

    private Entry? Find(DriveVolume volume) =>
        _entries.TryGetValue(volume.Letter, out Entry? entry) && SameVolume(entry.Volume, volume) ? entry : null;

    private Entry Claim(DriveVolume volume)
    {
        if (Find(volume) is { } entry)
        {
            return entry;
        }

        entry = new Entry(volume);
        _entries[volume.Letter] = entry;
        return entry;
    }

    private sealed class Entry(DriveVolume volume)
    {
        public DriveVolume Volume { get; } = volume;

        public Task<MediaProbe>? Media { get; set; }

        public Task<DriveHealth>? Health { get; set; }
    }
}

public enum ProbeStage
{
    Media,
    Slow,
    Failed,
    Health,
}

public sealed record DriveProbeUpdate(DriveVolume Volume, ProbeStage Stage, DriveHealth? Health = null, string? Error = null);

public sealed class DriveListLoader(DriveOptimizer optimizer, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    public IReadOnlyList<DriveVolume> Shell() => optimizer.ListVolumes();

    public Task ProbeAllAsync(IEnumerable<DriveVolume> volumes, Action<DriveProbeUpdate> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(update);
        return Task.WhenAll(volumes.Select(v => ProbeOneAsync(v, update, cancellationToken)));
    }

    public async Task ProbeOneAsync(DriveVolume volume, Action<DriveProbeUpdate> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(update);
        DriveVolume full = volume;
        if (!volume.Probed)
        {
            Task<DriveVolume> media = optimizer.ProbeAsync(volume);
            if (!media.IsCompleted)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task first = await Task.WhenAny(media, Task.Delay(Timeout, wait.Token)).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (first != media)
                {
                    update(new DriveProbeUpdate(volume, ProbeStage.Slow));
                }
            }

            try
            {
                full = await media.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                update(new DriveProbeUpdate(volume, ProbeStage.Failed, Error: ex.Message));
                return;
            }

            update(new DriveProbeUpdate(full, ProbeStage.Media));
        }

        DriveHealth health;
        try
        {
            health = await optimizer.ReadHealthAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            health = DriveHealth.Unavailable(full);
        }

        update(new DriveProbeUpdate(full, ProbeStage.Health, health));
    }

    public static bool IsProbeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or ArgumentException;
}
