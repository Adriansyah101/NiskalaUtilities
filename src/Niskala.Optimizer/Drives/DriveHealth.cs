using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public enum HealthSource
{
    None,
    Nvme,
    Ata,
    FailurePrediction,
    Temperature,
}

public enum HealthStatus
{
    Unknown,
    Healthy,
    Warning,
}

[Flags]
public enum HealthWarning
{
    None = 0,
    FailurePredicted = 1,
    SpareLow = 2,
    WornOut = 4,
    MediaErrors = 8,
    ReadOnly = 16,
    Overheating = 32,
    ReallocatedSectors = 64,
    PendingSectors = 128,
}

public sealed record DriveHealth(
    HealthSource Source,
    HealthWarning Warnings,
    int? TemperatureCelsius,
    long? PowerOnHours,
    int? LifeUsedPercent,
    int? AvailableSparePercent,
    bool Bridged = false)
{
    public static DriveHealth NotRead { get; } = new(HealthSource.None, HealthWarning.None, null, null, null, null);

    public HealthStatus Status => Warnings != HealthWarning.None ? HealthStatus.Warning
        : Source is HealthSource.Nvme or HealthSource.Ata or HealthSource.FailurePrediction ? HealthStatus.Healthy
        : HealthStatus.Unknown;

    public bool HasData => Source != HealthSource.None;

    public int? LifeRemainingPercent => LifeUsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;

    public static DriveHealth Unavailable(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return NotRead with { Bridged = volume.Bus is StorageBus.Usb or StorageBus.Ieee1394 };
    }
}

public static class SmartParser
{
    public const int AttributeCount = 30;
    public const int AttributeBytes = 12;
    public const int AttributeTableOffset = 2;
    public const int KelvinOffset = 273;
    public const int LowestCelsius = -40;
    public const int HighestCelsius = 150;
    public const int WornOutPercent = 100;

    public const byte PowerOnHoursId = 9;
    public const byte ReallocatedId = 5;
    public const byte PendingId = 197;
    public const byte UncorrectableId = 198;
    public const byte TemperatureId = 194;
    public const byte AirflowTemperatureId = 190;

    private const int NvmeCriticalWarning = 0;
    private const int NvmeTemperature = 1;
    private const int NvmeAvailableSpare = 3;
    private const int NvmeSpareThreshold = 4;
    private const int NvmePercentageUsed = 5;
    private const int NvmePowerOnHours = 128;
    private const int NvmeLogMinimum = NvmePowerOnHours + 16;
    private const int AttributeCurrent = 3;
    private const int AttributeRaw = 5;
    private const int HighestNormalized = 100;
    private const int RawBytes = 6;
    private const byte SpareBit = 0x01;
    private const byte TemperatureBit = 0x02;
    private const byte ReliabilityBits = 0x14;
    private const byte ReadOnlyBit = 0x08;

    private static readonly byte[] WearIds = [231, 233, 177, 202, 169];

    public static DriveHealth? FromNvme(ReadOnlySpan<byte> log)
    {
        if (log.Length < NvmeLogMinimum || log[..NvmeLogMinimum].IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        byte critical = log[NvmeCriticalWarning];
        HealthWarning warnings = HealthWarning.None;
        if ((critical & SpareBit) != 0)
        {
            warnings |= HealthWarning.SpareLow;
        }

        if ((critical & TemperatureBit) != 0)
        {
            warnings |= HealthWarning.Overheating;
        }

        if ((critical & ReliabilityBits) != 0)
        {
            warnings |= HealthWarning.MediaErrors;
        }

        if ((critical & ReadOnlyBit) != 0)
        {
            warnings |= HealthWarning.ReadOnly;
        }

        int spare = log[NvmeAvailableSpare];
        int threshold = log[NvmeSpareThreshold];
        if (threshold > 0 && spare < threshold)
        {
            warnings |= HealthWarning.SpareLow;
        }

        int used = log[NvmePercentageUsed];
        if (used >= WornOutPercent)
        {
            warnings |= HealthWarning.WornOut;
        }

        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[NvmeTemperature..]);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(log[(NvmePowerOnHours + 8)..]);
        ulong hours = BinaryPrimitives.ReadUInt64LittleEndian(log[NvmePowerOnHours..]);
        return new DriveHealth(
            HealthSource.Nvme,
            warnings,
            kelvin == 0 ? null : Celsius(kelvin - KelvinOffset),
            high != 0 || hours > long.MaxValue ? long.MaxValue : (long)hours,
            Math.Min(used, byte.MaxValue),
            spare <= HighestNormalized ? spare : null);
    }

    public static DriveHealth FromAta(bool predicted, ReadOnlySpan<byte> data, bool solidState)
    {
        HealthWarning warnings = predicted ? HealthWarning.FailurePredicted : HealthWarning.None;
        int? temperature = null, airflow = null, life = null;
        long? hours = null;
        bool table = false;
        int wearRank = WearIds.Length;
        for (int index = 0; index < AttributeCount; index++)
        {
            int offset = AttributeTableOffset + (index * AttributeBytes);
            if (offset + AttributeBytes > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> attribute = data.Slice(offset, AttributeBytes);
            byte id = attribute[0];
            if (id == 0)
            {
                continue;
            }

            table = true;
            long raw = Raw(attribute);
            switch (id)
            {
                case PowerOnHoursId:
                    hours = raw & uint.MaxValue;
                    break;
                case TemperatureId:
                    temperature = Celsius(attribute[AttributeRaw]);
                    break;
                case AirflowTemperatureId:
                    airflow = Celsius(attribute[AttributeRaw]);
                    break;
                case ReallocatedId when raw > 0:
                    warnings |= HealthWarning.ReallocatedSectors;
                    break;
                case PendingId when raw > 0:
                    warnings |= HealthWarning.PendingSectors;
                    break;
                case UncorrectableId when raw > 0:
                    warnings |= HealthWarning.MediaErrors;
                    break;
            }

            int rank = Array.IndexOf(WearIds, id);
            int current = attribute[AttributeCurrent];
            if (solidState && rank >= 0 && rank < wearRank && current is > 0 and <= HighestNormalized)
            {
                wearRank = rank;
                life = HighestNormalized - current;
            }
        }

        if (life >= WornOutPercent)
        {
            warnings |= HealthWarning.WornOut;
        }

        return new DriveHealth(table ? HealthSource.Ata : HealthSource.FailurePrediction, warnings, temperature ?? airflow, hours, life, null);
    }

    public static int? Celsius(int value) => value is > 0 and <= HighestCelsius || (value < 0 && value >= LowestCelsius) ? value : null;

    private static long Raw(ReadOnlySpan<byte> attribute)
    {
        long raw = 0;
        for (int i = RawBytes - 1; i >= 0; i--)
        {
            raw = (raw << 8) | attribute[AttributeRaw + i];
        }

        return raw;
    }
}

public interface IHealthProbe
{
    int ReadNvmeLog(Span<byte> log);

    int PredictFailure(out bool predicted, Span<byte> data);

    int ReadTemperature(out TemperatureFacts facts);
}

public interface IDriveHealthSource
{
    DriveHealth Read(DriveVolume volume);
}

public static class DriveHealthReader
{
    public static DriveHealth Read(DriveVolume volume, IHealthProbe probe)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(probe);
        DriveHealth? health = null;
        Span<byte> buffer = stackalloc byte[VolumeNative.HealthLogBytes];
        if (volume.Bus is StorageBus.Nvme or StorageBus.Unknown or StorageBus.Usb)
        {
            buffer.Clear();
            if (probe.ReadNvmeLog(buffer) == 0)
            {
                health = SmartParser.FromNvme(buffer);
            }
        }

        if (health is null)
        {
            buffer.Clear();
            if (probe.PredictFailure(out bool predicted, buffer) == 0)
            {
                health = SmartParser.FromAta(predicted, buffer, volume.Media == MediaKind.Ssd);
            }
        }

        if (probe.ReadTemperature(out TemperatureFacts facts) == 0 && SmartParser.Celsius(facts.Current) is { } celsius)
        {
            health ??= DriveHealth.Unavailable(volume) with { Source = HealthSource.Temperature };
            if (health.TemperatureCelsius is null)
            {
                health = health with { TemperatureCelsius = celsius };
            }

            if (facts.Warning > 0 && health.TemperatureCelsius >= facts.Warning)
            {
                health = health with { Warnings = health.Warnings | HealthWarning.Overheating };
            }
        }

        return health ?? DriveHealth.Unavailable(volume);
    }
}

public sealed class SystemDriveHealth : IDriveHealthSource
{
    private SystemDriveHealth()
    {
    }

    public static SystemDriveHealth Instance { get; } = new();

    public DriveHealth Read(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (VolumeNative.TryOpenDisk(volume.Letter, out SafeFileHandle disk) != 0)
        {
            disk.Dispose();
            return DriveHealth.Unavailable(volume);
        }

        using (disk)
        {
            return DriveHealthReader.Read(volume, new Probe(disk));
        }
    }

    private sealed class Probe(SafeFileHandle disk) : IHealthProbe
    {
        public int ReadNvmeLog(Span<byte> log) => VolumeNative.TryReadNvmeHealth(disk, log);

        public int PredictFailure(out bool predicted, Span<byte> data) => VolumeNative.TryPredictFailure(disk, out predicted, data);

        public int ReadTemperature(out TemperatureFacts facts) => VolumeNative.TryReadTemperature(disk, out facts);
    }
}
