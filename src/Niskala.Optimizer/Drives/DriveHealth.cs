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
    SatPassThrough,
    NvmeBridge,
}

public enum HealthProtocol
{
    Unknown,
    Nvme,
    Sata,
    Scsi,
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
    AttributeFailing = 256,
}

public sealed record AtaAttribute(byte Id, int Current, int Worst, int? Threshold, long Raw)
{
    public bool Failing => Threshold is { } threshold && threshold > 0 && Current > 0 && Current <= threshold;
}

public sealed record NvmeCounters(long DataUnitsRead, long DataUnitsWritten, long PowerCycles, long UnsafeShutdowns, long MediaErrors, long ErrorLogEntries)
{
    public const long BytesPerDataUnit = 512_000;
}

public sealed record DriveHealth(
    HealthSource Source,
    HealthWarning Warnings,
    int? TemperatureCelsius,
    long? PowerOnHours,
    int? LifeUsedPercent,
    int? AvailableSparePercent,
    bool Bridged = false,
    HealthProtocol Protocol = HealthProtocol.Unknown,
    string? Bridge = null,
    string? Model = null,
    IReadOnlyList<AtaAttribute>? Attributes = null,
    NvmeCounters? Counters = null,
    long? ReallocatedSectors = null,
    long? PendingSectors = null,
    long? UncorrectableSectors = null)
{
    public static DriveHealth NotRead { get; } = new(HealthSource.None, HealthWarning.None, null, null, null, null);

    public HealthStatus Status => Warnings != HealthWarning.None ? HealthStatus.Warning
        : Source is HealthSource.Nvme or HealthSource.Ata or HealthSource.FailurePrediction or HealthSource.SatPassThrough or HealthSource.NvmeBridge ? HealthStatus.Healthy
        : HealthStatus.Unknown;

    public bool HasData => Source != HealthSource.None;

    public int? LifeRemainingPercent => LifeUsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;

    public IReadOnlyList<AtaAttribute> Table => Attributes ?? [];

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
    private const int NvmeDataUnitsRead = 32;
    private const int NvmeDataUnitsWritten = 48;
    private const int NvmePowerCycles = 112;
    private const int NvmePowerOnHours = 128;
    private const int NvmeUnsafeShutdowns = 144;
    private const int NvmeMediaErrors = 160;
    private const int NvmeErrorEntries = 176;
    private const int NvmeCounterBytes = 16;
    private const int NvmeLogMinimum = NvmePowerOnHours + NvmeCounterBytes;
    private const int NvmeCountersMinimum = NvmeErrorEntries + NvmeCounterBytes;
    private const int AttributeWorst = 4;
    private const int ThresholdValue = 1;
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
        NvmeCounters? counters = log.Length >= NvmeCountersMinimum
            ? new NvmeCounters(
                Counter(log, NvmeDataUnitsRead),
                Counter(log, NvmeDataUnitsWritten),
                Counter(log, NvmePowerCycles),
                Counter(log, NvmeUnsafeShutdowns),
                Counter(log, NvmeMediaErrors),
                Counter(log, NvmeErrorEntries))
            : null;
        return new DriveHealth(
            HealthSource.Nvme,
            warnings,
            kelvin == 0 ? null : Celsius(kelvin - KelvinOffset),
            Counter(log, NvmePowerOnHours),
            Math.Min(used, byte.MaxValue),
            spare <= HighestNormalized ? spare : null,
            Protocol: HealthProtocol.Nvme,
            Counters: counters);
    }

    public static long Counter(ReadOnlySpan<byte> log, int offset)
    {
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(log[(offset + 8)..]);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);
        return high != 0 || low > long.MaxValue ? long.MaxValue : (long)low;
    }

    public static IReadOnlyDictionary<byte, int> Thresholds(ReadOnlySpan<byte> data)
    {
        var thresholds = new Dictionary<byte, int>();
        for (int index = 0; index < AttributeCount; index++)
        {
            int offset = AttributeTableOffset + (index * AttributeBytes);
            if (offset + AttributeBytes > data.Length)
            {
                break;
            }

            byte id = data[offset];
            if (id != 0)
            {
                thresholds[id] = data[offset + ThresholdValue];
            }
        }

        return thresholds;
    }

    public static DriveHealth FromAta(bool predicted, ReadOnlySpan<byte> data, bool solidState) => FromAta(predicted, data, solidState, ReadOnlySpan<byte>.Empty);

    public static DriveHealth FromAta(bool predicted, ReadOnlySpan<byte> data, bool solidState, ReadOnlySpan<byte> thresholdData)
    {
        IReadOnlyDictionary<byte, int> thresholds = Thresholds(thresholdData);
        var attributes = new List<AtaAttribute>();
        long? reallocated = null, pending = null, uncorrectable = null;
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
            var entry = new AtaAttribute(id, attribute[AttributeCurrent], attribute[AttributeWorst], thresholds.TryGetValue(id, out int limit) ? limit : null, raw);
            attributes.Add(entry);
            if (entry.Failing)
            {
                warnings |= HealthWarning.AttributeFailing;
            }

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
                case ReallocatedId:
                    reallocated = raw & uint.MaxValue;
                    warnings |= reallocated > 0 ? HealthWarning.ReallocatedSectors : HealthWarning.None;
                    break;
                case PendingId:
                    pending = raw & uint.MaxValue;
                    warnings |= pending > 0 ? HealthWarning.PendingSectors : HealthWarning.None;
                    break;
                case UncorrectableId:
                    uncorrectable = raw & uint.MaxValue;
                    warnings |= uncorrectable > 0 ? HealthWarning.MediaErrors : HealthWarning.None;
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

        return new DriveHealth(
            table ? HealthSource.Ata : HealthSource.FailurePrediction,
            warnings,
            temperature ?? airflow,
            hours,
            life,
            null,
            Protocol: HealthProtocol.Sata,
            Attributes: table ? attributes : null,
            ReallocatedSectors: reallocated,
            PendingSectors: pending,
            UncorrectableSectors: uncorrectable);
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

    string? UsbParent => null;

    int Command(ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
    {
        reply = new ScsiReply(0, 0, []);
        return VolumeNative.ErrorNotSupported;
    }
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
        UsbBridge? bridge = UsbBridge.Parse(probe.UsbParent);
        bool bridged = bridge is not null || volume.Bus is StorageBus.Usb or StorageBus.Ieee1394;
        string? model = null;
        Span<byte> buffer = stackalloc byte[VolumeNative.HealthLogBytes];
        if (volume.Bus is StorageBus.Nvme or StorageBus.Unknown or StorageBus.Usb || bridged)
        {
            buffer.Clear();
            if (probe.ReadNvmeLog(buffer) == 0)
            {
                health = SmartParser.FromNvme(buffer);
            }
        }

        if (health is null && bridge is { CarriesNvme: true } nvme)
        {
            buffer.Clear();
            if (BridgeReader.ReadNvmeHealth(probe, nvme.Kind, buffer))
            {
                health = SmartParser.FromNvme(buffer) is { } read ? read with { Source = HealthSource.NvmeBridge } : null;
            }
        }

        if (health is null && bridge is not { CarriesNvme: true } && (bridged || volume.Bus is StorageBus.Scsi or StorageBus.Unknown))
        {
            if (BridgeReader.ReadSat(probe) is { } sat)
            {
                model = sat.Identity?.Model;
                if (sat.Smart is { } smart)
                {
                    bool solid = sat.Identity?.IsSolidState == true || volume.Media == MediaKind.Ssd;
                    health = SmartParser.FromAta(false, smart, solid, sat.Thresholds) with { Source = HealthSource.SatPassThrough };
                }
            }
        }

        if (health is null)
        {
            buffer.Clear();
            if (probe.PredictFailure(out bool predicted, buffer) == 0)
            {
                health = SmartParser.FromAta(predicted, buffer, volume.Media == MediaKind.Ssd);
                if (volume.Bus == StorageBus.Nvme)
                {
                    health = health with { Protocol = HealthProtocol.Nvme };
                }
                else if (volume.Bus is StorageBus.Scsi or StorageBus.Sas)
                {
                    health = health with { Protocol = HealthProtocol.Scsi };
                }
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

        health ??= DriveHealth.Unavailable(volume);
        return health with
        {
            Bridged = bridged,
            Bridge = bridge?.Name,
            Model = health.Model ?? model,
            Protocol = health.Protocol != HealthProtocol.Unknown ? health.Protocol : volume.Bus == StorageBus.Nvme ? HealthProtocol.Nvme : HealthProtocol.Unknown,
        };
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
            using var probe = new DiskProbe(volume.Letter, disk);
            return DriveHealthReader.Read(volume, probe);
        }
    }
}

public sealed class DiskProbe(char letter, SafeFileHandle disk) : IHealthProbe, IDisposable
{
    private SafeFileHandle? _commands;
    private bool _opened;
    private string? _usb;
    private bool _looked;

    public string? UsbParent
    {
        get
        {
            if (!_looked)
            {
                _looked = true;
                _usb = VolumeNative.TryGetDiskNumber(letter, out uint number) == 0 && VolumeNative.TryGetUsbParent(number, out string id) == 0 ? id : null;
            }

            return _usb;
        }
    }

    public int ReadNvmeLog(Span<byte> log) => VolumeNative.TryReadNvmeHealth(disk, log);

    public int PredictFailure(out bool predicted, Span<byte> data) => VolumeNative.TryPredictFailure(disk, out predicted, data);

    public int ReadTemperature(out TemperatureFacts facts) => VolumeNative.TryReadTemperature(disk, out facts);

    public int Command(ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
    {
        reply = new ScsiReply(0, 0, []);
        if (!_opened)
        {
            _opened = true;
            if (VolumeNative.TryOpenDiskForCommands(letter, out SafeFileHandle commands) == 0)
            {
                _commands = commands;
            }
            else
            {
                commands.Dispose();
            }
        }

        return _commands is { } handle ? VolumeNative.TryScsiCommand(handle, cdb, direction, data, out reply) : VolumeNative.ErrorAccessDenied;
    }

    public void Dispose() => _commands?.Dispose();
}
