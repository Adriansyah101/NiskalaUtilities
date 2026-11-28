using System.Diagnostics.CodeAnalysis;

namespace Niskala.Optimizer.Drives;

public sealed record CachedAnalysis(DriveAnalysis Analysis, DateTimeOffset ScannedAt, DateTimeOffset UpdatedAt, int Updates);

public sealed class VolumeAnalysisCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<(uint Serial, char Letter), CachedAnalysis> _entries = [];

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

    public static bool Matches(DriveVolume cached, DriveVolume current)
    {
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(current);
        return cached.SerialNumber == current.SerialNumber
            && cached.Letter == current.Letter
            && cached.TotalClusters == current.TotalClusters
            && cached.BytesPerCluster == current.BytesPerCluster
            && string.Equals(cached.FileSystem, current.FileSystem, StringComparison.OrdinalIgnoreCase);
    }

    public CachedAnalysis Store(DriveAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        DateTimeOffset now = _clock.GetUtcNow();
        var entry = new CachedAnalysis(analysis, now, now, 0);
        lock (_gate)
        {
            _entries[Key(analysis.Volume)] = entry;
        }

        return entry;
    }

    public bool TryGet(DriveVolume volume, [NotNullWhen(true)] out CachedAnalysis? entry)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (_gate)
        {
            if (_entries.TryGetValue(Key(volume), out entry) && Matches(entry.Analysis.Volume, volume))
            {
                return true;
            }

            _entries.Remove(Key(volume));
            foreach (KeyValuePair<(uint Serial, char Letter), CachedAnalysis> stale in _entries.Where(e => e.Key.Letter == volume.Letter).ToArray())
            {
                _entries.Remove(stale.Key);
            }

            entry = null;
            return false;
        }
    }

    public DriveAnalysis? Find(DriveVolume volume) => TryGet(volume, out CachedAnalysis? entry) ? entry.Analysis : null;

    public bool Touch(DriveAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        lock (_gate)
        {
            (uint, char) key = Key(analysis.Volume);
            if (!_entries.TryGetValue(key, out CachedAnalysis? entry) || !ReferenceEquals(entry.Analysis, analysis))
            {
                return false;
            }

            _entries[key] = entry with { UpdatedAt = _clock.GetUtcNow(), Updates = entry.Updates + 1 };
            return true;
        }
    }

    public bool Forget(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        lock (_gate)
        {
            return _entries.Remove(Key(volume));
        }
    }

    private static (uint Serial, char Letter) Key(DriveVolume volume) => (volume.SerialNumber, volume.Letter);
}
