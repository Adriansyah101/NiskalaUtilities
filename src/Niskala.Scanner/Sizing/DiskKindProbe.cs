using System.Collections.Concurrent;
using Niskala.Interop.Storage;

namespace Niskala.Scanner.Sizing;

public enum DiskKind
{
    Unknown,
    Ssd,
    Hdd,
}

public readonly record struct VolumeInfo(string Volume, DiskKind Kind);

public sealed class DiskKindProbe
{
    private readonly ConcurrentDictionary<string, DiskKind> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    public VolumeInfo Probe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (StorageNative.TryGetVolumeDevice(path, out string device) != 0)
        {
            return new VolumeInfo(Path.GetPathRoot(path) ?? path, DiskKind.Unknown);
        }

        DiskKind kind = _byVolume.GetOrAdd(device, static volume =>
            StorageNative.TryQuerySeekPenalty(volume, out bool seekPenalty) != 0 ? DiskKind.Unknown
            : seekPenalty ? DiskKind.Hdd
            : DiskKind.Ssd);
        return new VolumeInfo(device, kind);
    }

    public static int WorkersFor(DiskKind kind) => kind switch
    {
        DiskKind.Ssd => 4,
        DiskKind.Hdd => 1,
        _ => 2,
    };
}
