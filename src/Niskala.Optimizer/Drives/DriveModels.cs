namespace Niskala.Optimizer.Drives;

public enum MediaKind
{
    Unknown,
    Hdd,
    Ssd,
}

public enum DriveAction
{
    AnalyzeOnly,
    Defragment,
    Trim,
}

public enum DefragMode
{
    Quick,
    Consolidate,
    FullZone,
}

public enum SpeedZone
{
    Fast,
    Standard,
    Archive,
}

public enum ClusterState : byte
{
    Free,
    Used,
    Fragmented,
    Archive,
    System,
}

public sealed record DriveVolume(
    char Letter,
    string Label,
    string FileSystem,
    long BytesPerCluster,
    long TotalClusters,
    long TotalBytes,
    long FreeBytes,
    MediaKind Media,
    bool TrimSupported,
    StorageBus Bus = StorageBus.Unknown,
    int RotationRate = MediaClassifier.RotationNotReported)
{
    private static readonly string[] MovableFileSystems = ["NTFS", "FAT", "FAT32", "exFAT"];

    public string Name => $"{Letter}:";

    public string Root => $"{Letter}:\\";

    public bool SupportsMoves => MovableFileSystems.Contains(FileSystem, StringComparer.OrdinalIgnoreCase);

    public bool CanRelocate => Media == MediaKind.Hdd && SupportsMoves;

    public DriveAction DefaultAction => Media switch
    {
        MediaKind.Ssd => DriveAction.Trim,
        MediaKind.Hdd when SupportsMoves => DriveAction.Defragment,
        _ => DriveAction.AnalyzeOnly,
    };
}

public readonly record struct Extent(long Vcn, long Lcn, long Clusters)
{
    public long End => Lcn + Clusters;

    public bool IsVirtual => Lcn < 0;
}

public readonly record struct FreeRun(long Start, long Length)
{
    public long End => Start + Length;
}

public enum DrivePhase
{
    Opening,
    ReadingMap,
    Scanning,
    Moving,
    Compacting,
    Trimming,
    Finished,
}

public readonly record struct DriveProgress(DrivePhase Phase, double Percent, string Current, long MapVersion, DriveMap? Map);

public enum MoveOutcome
{
    Moved,
    AlreadyPlaced,
    Locked,
    NoRoom,
    Failed,
}

public sealed record DefragResult(
    DefragMode Mode,
    int Planned,
    int Moved,
    int Defragmented,
    int Locked,
    int NoRoom,
    int Failed,
    long ClustersMoved,
    bool Cancelled,
    TimeSpan Elapsed);

public enum TrimMethod
{
    None,
    FileLevel,
    SystemRetrim,
}

public enum TrimOutcome
{
    Trimmed,
    NotSupported,
    Failed,
    Cancelled,
}

public sealed record TrimResult(TrimOutcome Outcome, TrimMethod Method, long Bytes, int Error, TimeSpan Elapsed);

public sealed class DriveRelocationRefusedException(string message) : InvalidOperationException(message);
