using System.Collections.Frozen;

namespace Niskala.Optimizer.Drives;

public static class ZoneClassifier
{
    public const long LargeFileBytes = 1L << 30;

    public static readonly TimeSpan SettledAge = TimeSpan.FromDays(90);

    private static readonly FrozenSet<string> FastExtensions = FrozenSet.ToFrozenSet(
        [".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl", ".efi", ".mui", ".winmd", ".com", ".scr"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ArchiveExtensions = FrozenSet.ToFrozenSet(
        [".iso", ".img", ".zip", ".7z", ".rar", ".tar", ".gz", ".cab", ".wim", ".esd", ".vhd", ".vhdx", ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".bak"],
        StringComparer.OrdinalIgnoreCase);

    public static SpeedZone Classify(string path, long size, DateTime lastWriteUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        if (FastExtensions.Contains(extension))
        {
            return SpeedZone.Fast;
        }

        if (ArchiveExtensions.Contains(extension) || (size >= LargeFileBytes && nowUtc - lastWriteUtc >= SettledAge))
        {
            return SpeedZone.Archive;
        }

        return SpeedZone.Standard;
    }
}

public readonly record struct ZoneLayout(long FastEnd, long ArchiveStart, long Clusters)
{
    private const int FastSlackDivisor = 4;
    private const int ArchiveSlackDivisor = 8;
    private const int VolumeMarginDivisor = 100;

    public static ZoneLayout For(long clusters, long fast, long archive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusters);
        long margin = clusters / VolumeMarginDivisor;
        long fastEnd = Math.Min(clusters, fast + (fast / FastSlackDivisor) + margin);
        long archiveStart = Math.Max(fastEnd, clusters - archive - (archive / ArchiveSlackDivisor) - margin);
        return new ZoneLayout(fastEnd, archiveStart, clusters);
    }

    public SpeedZone ZoneOf(long lcn) => lcn < FastEnd ? SpeedZone.Fast : lcn >= ArchiveStart ? SpeedZone.Archive : SpeedZone.Standard;

    public (long From, long To) Range(SpeedZone zone) => zone switch
    {
        SpeedZone.Fast => (0, FastEnd),
        SpeedZone.Archive => (ArchiveStart, Clusters),
        _ => (FastEnd, ArchiveStart),
    };

    public bool Holds(SpeedZone zone, long lcn, long clusters)
    {
        (long from, long to) = Range(zone);
        return lcn >= from && lcn + clusters <= to;
    }
}

public static class ProtectedFiles
{
    public const string VolumeInformation = "System Volume Information";

    private static readonly FrozenSet<string> SystemNames = FrozenSet.ToFrozenSet(
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend", VolumeInformation],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SystemNameSpans = SystemNames.GetAlternateLookup<ReadOnlySpan<char>>();

    public static IReadOnlyCollection<string> Names => SystemNames;

    public static bool IsProtectedName(ReadOnlySpan<char> name) => SystemNameSpans.Contains(name);

    public static bool IsProtected(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ReadOnlySpan<char> path = relativePath;
        foreach (Range part in path.SplitAny('\\', '/'))
        {
            if (IsProtectedName(path[part]))
            {
                return true;
            }
        }

        return false;
    }
}

public readonly record struct MoveStep(long Vcn, long Lcn, uint Clusters);

public static class MoveChunks
{
    public const uint MaxClusters = 16384;

    public static IEnumerable<MoveStep> Plan(long clusters, long targetLcn, uint limit = MaxClusters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusters);
        ArgumentOutOfRangeException.ThrowIfNegative(targetLcn);
        ArgumentOutOfRangeException.ThrowIfZero(limit);
        for (long vcn = 0; vcn < clusters; vcn += limit)
        {
            yield return new MoveStep(vcn, targetLcn + vcn, (uint)Math.Min(limit, clusters - vcn));
        }
    }
}
