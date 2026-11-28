using System.Buffers.Binary;
using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class DriveHealthTests
{
    private const int NotSupported = VolumeNative.ErrorNotSupported;

    private static DriveVolume Volume(MediaKind media, StorageBus bus) =>
        new('C', "System", "NTFS", 4096, 1_000_000, 4_096_000_000, 1_000_000_000, media, media == MediaKind.Ssd, bus);

    private static byte[] NvmeLog(byte critical = 0, int kelvin = 317, byte spare = 100, byte threshold = 10, byte used = 4, ulong hours = 5_455)
    {
        byte[] log = new byte[VolumeNative.HealthLogBytes];
        log[0] = critical;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), (ushort)kelvin);
        log[3] = spare;
        log[4] = threshold;
        log[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), hours);
        return log;
    }

    private static byte[] AtaTable(params (byte Id, byte Current, long Raw)[] attributes)
    {
        byte[] data = new byte[VolumeNative.FailureDataBytes];
        for (int i = 0; i < attributes.Length; i++)
        {
            int offset = SmartParser.AttributeTableOffset + (i * SmartParser.AttributeBytes);
            data[offset] = attributes[i].Id;
            data[offset + 3] = attributes[i].Current;
            data[offset + 4] = attributes[i].Current;
            for (int b = 0; b < 6; b++)
            {
                data[offset + 5 + b] = (byte)(attributes[i].Raw >> (8 * b));
            }
        }

        return data;
    }

    [Fact]
    public void An_nvme_health_log_gives_temperature_hours_wear_and_spare()
    {
        DriveHealth health = SmartParser.FromNvme(NvmeLog())!;

        Assert.Equal(HealthSource.Nvme, health.Source);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(44, health.TemperatureCelsius);
        Assert.Equal(5_455, health.PowerOnHours);
        Assert.Equal(4, health.LifeUsedPercent);
        Assert.Equal(96, health.LifeRemainingPercent);
        Assert.Equal(100, health.AvailableSparePercent);
        Assert.Equal(HealthWarning.None, health.Warnings);
    }

    [Theory]
    [InlineData(0x01, HealthWarning.SpareLow)]
    [InlineData(0x02, HealthWarning.Overheating)]
    [InlineData(0x04, HealthWarning.MediaErrors)]
    [InlineData(0x08, HealthWarning.ReadOnly)]
    [InlineData(0x10, HealthWarning.MediaErrors)]
    [InlineData(0x0B, HealthWarning.SpareLow | HealthWarning.Overheating | HealthWarning.ReadOnly)]
    public void Nvme_critical_warning_bits_raise_a_warning(byte critical, HealthWarning expected)
    {
        DriveHealth health = SmartParser.FromNvme(NvmeLog(critical))!;
        Assert.Equal(expected, health.Warnings);
        Assert.Equal(HealthStatus.Warning, health.Status);
    }

    [Fact]
    public void A_worn_nvme_drive_or_one_below_its_spare_threshold_is_flagged()
    {
        Assert.Equal(HealthWarning.WornOut, SmartParser.FromNvme(NvmeLog(used: 100))!.Warnings);
        Assert.Equal(0, SmartParser.FromNvme(NvmeLog(used: 130))!.LifeRemainingPercent);
        Assert.Equal(HealthWarning.SpareLow, SmartParser.FromNvme(NvmeLog(spare: 5, threshold: 10))!.Warnings);
        Assert.Equal(HealthWarning.None, SmartParser.FromNvme(NvmeLog(spare: 5, threshold: 0))!.Warnings);
    }

    [Fact]
    public void An_empty_or_short_nvme_log_is_no_data_and_odd_temperatures_are_dropped()
    {
        Assert.Null(SmartParser.FromNvme(new byte[VolumeNative.HealthLogBytes]));
        Assert.Null(SmartParser.FromNvme(new byte[64]));
        Assert.Null(SmartParser.FromNvme(NvmeLog(kelvin: 0))!.TemperatureCelsius);
        Assert.Null(SmartParser.FromNvme(NvmeLog(kelvin: 1_000))!.TemperatureCelsius);
        Assert.Equal(long.MaxValue, SmartParser.FromNvme(NvmeLog(hours: ulong.MaxValue))!.PowerOnHours);
    }

    [Fact]
    public void Ata_attributes_give_temperature_hours_and_ssd_wear()
    {
        byte[] data = AtaTable((SmartParser.PowerOnHoursId, 98, 12_345), (SmartParser.TemperatureId, 70, 0x0000_0021_0012_0021), (231, 87, 0), (177, 60, 0));
        DriveHealth health = SmartParser.FromAta(false, data, solidState: true);

        Assert.Equal(HealthSource.Ata, health.Source);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(33, health.TemperatureCelsius);
        Assert.Equal(12_345, health.PowerOnHours);
        Assert.Equal(13, health.LifeUsedPercent);
        Assert.Equal(87, health.LifeRemainingPercent);
    }

    [Fact]
    public void Hard_disks_ignore_wear_attributes_and_fall_back_to_the_airflow_sensor()
    {
        DriveHealth health = SmartParser.FromAta(false, AtaTable((177, 40, 0), (SmartParser.AirflowTemperatureId, 60, 38)), solidState: false);

        Assert.Null(health.LifeUsedPercent);
        Assert.Equal(38, health.TemperatureCelsius);
    }

    [Theory]
    [InlineData(SmartParser.ReallocatedId, HealthWarning.ReallocatedSectors)]
    [InlineData(SmartParser.PendingId, HealthWarning.PendingSectors)]
    [InlineData(SmartParser.UncorrectableId, HealthWarning.MediaErrors)]
    public void Bad_sector_counters_raise_a_warning(byte id, HealthWarning warning)
    {
        Assert.Equal(warning, SmartParser.FromAta(false, AtaTable((id, 100, 3)), solidState: false).Warnings);
        Assert.Equal(HealthWarning.None, SmartParser.FromAta(false, AtaTable((id, 100, 0)), solidState: false).Warnings);
    }

    [Fact]
    public void A_predicted_failure_is_a_warning_even_without_attributes()
    {
        DriveHealth predicted = SmartParser.FromAta(true, new byte[VolumeNative.FailureDataBytes], solidState: false);
        DriveHealth calm = SmartParser.FromAta(false, new byte[VolumeNative.FailureDataBytes], solidState: false);

        Assert.Equal((HealthSource.FailurePrediction, HealthStatus.Warning, HealthWarning.FailurePredicted), (predicted.Source, predicted.Status, predicted.Warnings));
        Assert.Equal((HealthSource.FailurePrediction, HealthStatus.Healthy), (calm.Source, calm.Status));
    }

    [Fact]
    public void The_reader_prefers_the_nvme_log_and_adds_the_temperature_limit()
    {
        var probe = new FakeProbe { Nvme = NvmeLog(kelvin: 360), Temperature = new TemperatureFacts(87, 80, 90) };
        DriveHealth health = DriveHealthReader.Read(Volume(MediaKind.Ssd, StorageBus.Nvme), probe);

        Assert.Equal(HealthSource.Nvme, health.Source);
        Assert.Equal(87, health.TemperatureCelsius);
        Assert.True(health.Warnings.HasFlag(HealthWarning.Overheating));
        Assert.Equal(0, probe.PredictCalls);
    }

    [Fact]
    public void Sata_drives_go_straight_to_failure_prediction()
    {
        var probe = new FakeProbe { Nvme = NvmeLog(), Ata = AtaTable((SmartParser.PowerOnHoursId, 100, 77)), Temperature = new TemperatureFacts(31, 0, 0) };
        DriveHealth health = DriveHealthReader.Read(Volume(MediaKind.Hdd, StorageBus.Sata), probe);

        Assert.Equal(0, probe.NvmeCalls);
        Assert.Equal(HealthSource.Ata, health.Source);
        Assert.Equal(77, health.PowerOnHours);
        Assert.Equal(31, health.TemperatureCelsius);
        Assert.Equal(HealthStatus.Healthy, health.Status);
    }

    [Fact]
    public void A_usb_bridge_that_passes_nothing_falls_back_gracefully()
    {
        var probe = new FakeProbe();
        DriveHealth health = DriveHealthReader.Read(Volume(MediaKind.Hdd, StorageBus.Usb), probe);

        Assert.False(health.HasData);
        Assert.True(health.Bridged);
        Assert.Equal(HealthStatus.Unknown, health.Status);
        Assert.Equal(1, probe.NvmeCalls);
        Assert.Equal(1, probe.PredictCalls);
        Assert.False(DriveHealth.Unavailable(Volume(MediaKind.Hdd, StorageBus.Sata)).Bridged);
    }

    [Fact]
    public void A_temperature_sensor_alone_still_shows_the_temperature()
    {
        var probe = new FakeProbe { Temperature = new TemperatureFacts(41, 0, 0) };
        DriveHealth health = DriveHealthReader.Read(Volume(MediaKind.Ssd, StorageBus.Usb), probe);

        Assert.Equal(HealthSource.Temperature, health.Source);
        Assert.Equal(41, health.TemperatureCelsius);
        Assert.Equal(HealthStatus.Unknown, health.Status);
        Assert.True(health.HasData);
    }

    [Fact]
    public async Task The_optimizer_reads_health_from_its_source_and_reports_none_without_one()
    {
        var disk = new FakeDisk(100, MediaKind.Ssd);
        var source = new FakeSource();
        DriveHealth read = await new DriveOptimizer(disk, health: source).ReadHealthAsync(disk.Volume, TestContext.Current.CancellationToken);
        DriveHealth none = await new DriveOptimizer(disk).ReadHealthAsync(disk.Volume, TestContext.Current.CancellationToken);

        Assert.Equal(HealthSource.Nvme, read.Source);
        Assert.Equal(1, source.Reads);
        Assert.False(none.HasData);
    }

    [Fact]
    public void The_real_reader_never_throws_on_this_pc()
    {
        foreach (DriveVolume volume in SystemVolumes.Instance.FixedVolumes())
        {
            DriveHealth health = SystemDriveHealth.Instance.Read(volume);
            Assert.True(health.TemperatureCelsius is null or (>= SmartParser.LowestCelsius and <= SmartParser.HighestCelsius));
            Assert.True(health.LifeUsedPercent is null or >= 0);
        }
    }

    private sealed class FakeProbe : IHealthProbe
    {
        public byte[]? Nvme { get; init; }

        public byte[]? Ata { get; init; }

        public bool Predicted { get; init; }

        public TemperatureFacts? Temperature { get; init; }

        public int NvmeCalls { get; private set; }

        public int PredictCalls { get; private set; }

        public int ReadNvmeLog(Span<byte> log)
        {
            NvmeCalls++;
            if (Nvme is null)
            {
                return NotSupported;
            }

            Nvme.CopyTo(log);
            return 0;
        }

        public int PredictFailure(out bool predicted, Span<byte> data)
        {
            PredictCalls++;
            predicted = Predicted;
            if (Ata is null)
            {
                return NotSupported;
            }

            Ata.CopyTo(data);
            return 0;
        }

        public int ReadTemperature(out TemperatureFacts facts)
        {
            facts = Temperature ?? default;
            return Temperature is null ? NotSupported : 0;
        }
    }

    private sealed class FakeSource : IDriveHealthSource
    {
        public int Reads { get; private set; }

        public DriveHealth Read(DriveVolume volume)
        {
            Reads++;
            return SmartParser.FromNvme(NvmeLog())!;
        }
    }
}
