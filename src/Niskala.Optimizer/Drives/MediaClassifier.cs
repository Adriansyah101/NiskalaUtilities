namespace Niskala.Optimizer.Drives;

public enum StorageBus
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    Iscsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

public readonly record struct MediaEvidence(bool? SeekPenalty, StorageBus Bus, int RotationRate, string Device)
{
    public static MediaEvidence None { get; } = new(null, StorageBus.Unknown, MediaClassifier.RotationNotReported, string.Empty);
}

public static class MediaClassifier
{
    public const int RotationNotReported = 0;
    public const int NonRotating = 1;
    public const int LowestRpm = 0x0401;
    public const int HighestRpm = 0xFFFE;

    private static readonly StorageBus[] FlashBuses = [StorageBus.Nvme, StorageBus.Sd, StorageBus.Mmc, StorageBus.Scm, StorageBus.Ufs];
    private static readonly StorageBus[] BridgedBuses = [StorageBus.Usb, StorageBus.Ieee1394, StorageBus.Unknown];
    private static readonly string[] SolidStateNames = ["SSD", "Solid State", "NVMe"];

    public static bool IsRpm(int rotationRate) => rotationRate is >= LowestRpm and <= HighestRpm;

    public static bool IsFlashBus(StorageBus bus) => FlashBuses.Contains(bus);

    public static bool IsSolidStateName(string? device) =>
        !string.IsNullOrWhiteSpace(device) && SolidStateNames.Any(name => device.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static bool WantsRotationRate(MediaEvidence evidence) =>
        !IsFlashBus(evidence.Bus) && (evidence.SeekPenalty != true || BridgedBuses.Contains(evidence.Bus));

    public static MediaKind Classify(MediaEvidence evidence)
    {
        if (IsFlashBus(evidence.Bus) || evidence.RotationRate == NonRotating || IsSolidStateName(evidence.Device))
        {
            return MediaKind.Ssd;
        }

        if (IsRpm(evidence.RotationRate) || evidence.SeekPenalty == true)
        {
            return MediaKind.Hdd;
        }

        return evidence.SeekPenalty == false ? MediaKind.Ssd : MediaKind.Unknown;
    }
}
