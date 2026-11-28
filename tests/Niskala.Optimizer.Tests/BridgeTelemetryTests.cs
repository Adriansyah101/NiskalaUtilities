using System.Buffers.Binary;
using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class BridgeTelemetryTests
{
    private const int NotSupported = VolumeNative.ErrorNotSupported;
    private const string SataBridge = @"USB\VID_1058&PID_2626\57583432";
    private const string JMicronNvme = @"USB\VID_152D&PID_0583\DD5641988";
    private const string AsmediaNvme = @"USB\VID_174C&PID_2362\MSFT30";
    private const string RealtekNvme = @"USB\VID_0BDA&PID_9210\012345";

    private static DriveVolume UsbVolume(MediaKind media = MediaKind.Hdd) =>
        new('E', "Backup", "NTFS", 4096, 1_000_000, 4_096_000_000, 2_048_000_000, media, false, StorageBus.Usb, 4800);

    [Fact]
    public void Sixteen_byte_ata_pass_through_carries_smart_read_data_in_the_right_registers()
    {
        byte[] cdb = SatCommand.SmartRead(sixteen: true, SatCommand.SmartReadData);

        Assert.Equal(16, cdb.Length);
        Assert.Equal(0x85, cdb[0]);
        Assert.Equal(0x08, cdb[1]);
        Assert.Equal(0x0E, cdb[2]);
        Assert.Equal(0xD0, cdb[4]);
        Assert.Equal(1, cdb[6]);
        Assert.Equal(0x4F, cdb[10]);
        Assert.Equal(0xC2, cdb[12]);
        Assert.Equal(0xA0, cdb[13]);
        Assert.Equal(0xB0, cdb[14]);
        Assert.Equal(0, cdb[15]);
        Assert.Equal(0xD1, SatCommand.SmartRead(true, SatCommand.SmartReadThresholds)[4]);
    }

    [Fact]
    public void Twelve_byte_ata_pass_through_packs_the_same_command_into_the_short_layout()
    {
        byte[] cdb = SatCommand.SmartRead(sixteen: false, SatCommand.SmartReadThresholds);

        Assert.Equal([0xA1, 0x08, 0x0E, 0xD1, 0x01, 0x00, 0x4F, 0xC2, 0xA0, 0xB0, 0x00, 0x00], cdb);
        byte[] identify = SatCommand.Identify(sixteen: false);
        Assert.Equal(0xEC, identify[9]);
        Assert.Equal(0, identify[3]);
        Assert.Equal(0xEC, SatCommand.Identify(sixteen: true)[14]);
    }

    [Fact]
    public void Smart_sectors_are_checked_by_checksum_and_shape()
    {
        byte[] smart = SmartSector((5, 100, 100, 0), (9, 100, 100, 427));
        Assert.True(SatCommand.ChecksumMatches(smart));
        Assert.True(SatCommand.LooksLikeSmartData(smart));
        Assert.False(SatCommand.LooksLikeSmartData(new byte[512]));
        Assert.False(SatCommand.LooksLikeSmartData(Enumerable.Repeat((byte)0xFF, 512).ToArray()));
        Assert.False(SatCommand.LooksLikeSmartData(new byte[100]));

        smart[511]++;
        Assert.False(SatCommand.ChecksumMatches(smart));
        Assert.True(SatCommand.LooksLikeSmartData(smart));
    }

    [Theory]
    [InlineData(new byte[] { 0x70, 0, 0x05, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x20, 0x00 }, SenseKey.IllegalRequest, 0x20, 0x00)]
    [InlineData(new byte[] { 0x72, 0x01, 0x00, 0x1D }, SenseKey.Recovered, 0x00, 0x1D)]
    [InlineData(new byte[] { 0xF0, 0, 0x03, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x11, 0x04 }, SenseKey.MediumError, 0x11, 0x04)]
    public void Fixed_and_descriptor_sense_data_both_parse(byte[] sense, SenseKey key, int asc, int ascq) =>
        Assert.Equal(new ScsiSense(key, (byte)asc, (byte)ascq), ScsiSense.Parse(sense));

    [Fact]
    public void Good_status_and_recovered_check_conditions_succeed_while_illegal_requests_are_rejections()
    {
        byte[] illegal = [0x70, 0, 0x05, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x20, 0x00];
        Assert.True(ScsiSense.Succeeded(new ScsiReply(0, 512, [])));
        Assert.True(ScsiSense.Succeeded(new ScsiReply(2, 512, [0x72, 0x01, 0x00, 0x1D])));
        Assert.False(ScsiSense.Succeeded(new ScsiReply(2, 0, illegal)));
        Assert.True(ScsiSense.Rejected(new ScsiReply(2, 0, illegal)));
        Assert.False(ScsiSense.Rejected(new ScsiReply(2, 0, [0x70, 0, 0x04, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x44, 0])));
        Assert.Null(ScsiSense.Parse([]));
        Assert.Null(ScsiSense.Parse([0x7F, 0, 0]));
    }

    [Fact]
    public void Identify_data_names_the_drive_and_its_spindle()
    {
        AtaIdentity? hdd = AtaIdentity.Parse(IdentifySector("WDC WD50NDZW-11BCSS0", 5400, smart: true, trim: false));
        AtaIdentity? ssd = AtaIdentity.Parse(IdentifySector("Portable SSD T7", MediaClassifier.NonRotating, smart: true, trim: true));

        Assert.NotNull(hdd);
        Assert.Equal("WDC WD50NDZW-11BCSS0", hdd.Model);
        Assert.Equal("SN123", hdd.Serial);
        Assert.Equal("01.01A01", hdd.Firmware);
        Assert.Equal(5400, hdd.RotationRate);
        Assert.True(hdd.SmartSupported);
        Assert.True(hdd.SmartEnabled);
        Assert.False(hdd.IsSolidState);
        Assert.NotNull(ssd);
        Assert.True(ssd.IsSolidState);
        Assert.True(ssd.TrimSupported);
    }

    [Fact]
    public void Identify_data_with_a_broken_integrity_word_or_no_model_is_ignored()
    {
        byte[] sector = IdentifySector("Disk", 7200, smart: true, trim: false);
        sector[100]++;
        Assert.Null(AtaIdentity.Parse(sector));
        Assert.Null(AtaIdentity.Parse(new byte[512]));
        Assert.Null(AtaIdentity.Parse(IdentifySector(string.Empty, 7200, smart: true, trim: false)));
    }

    [Theory]
    [InlineData(SataBridge, 0x1058, 0x2626, BridgeKind.Sata, false, "Western Digital")]
    [InlineData(JMicronNvme, 0x152D, 0x0583, BridgeKind.JMicronNvme, true, "JMicron")]
    [InlineData(AsmediaNvme, 0x174C, 0x2362, BridgeKind.AsmediaNvme, true, "ASMedia")]
    [InlineData(RealtekNvme, 0x0BDA, 0x9210, BridgeKind.RealtekNvme, true, "Realtek")]
    [InlineData(@"USB\VID_152D&PID_0578\1", 0x152D, 0x0578, BridgeKind.Sata, false, "JMicron")]
    [InlineData(@"USB\VID_ABCD&PID_0001\1", 0xABCD, 0x0001, BridgeKind.Unknown, false, "ABCD")]
    public void Usb_hardware_ids_name_the_bridge_and_whether_it_carries_nvme(string id, int vendor, int product, BridgeKind kind, bool nvme, string name)
    {
        UsbBridge? bridge = UsbBridge.Parse(id);

        Assert.NotNull(bridge);
        Assert.Equal((ushort)vendor, bridge.Value.Vendor);
        Assert.Equal((ushort)product, bridge.Value.Product);
        Assert.Equal(kind, bridge.Value.Kind);
        Assert.Equal(nvme, bridge.Value.CarriesNvme);
        Assert.StartsWith(name, bridge.Value.Name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"SCSI\Disk&Ven_WD")]
    [InlineData(@"USB\VID_12")]
    [InlineData(@"USB\VID_ZZZZ&PID_0001")]
    public void Anything_that_is_not_a_usb_id_has_no_bridge(string? id) => Assert.Null(UsbBridge.Parse(id));

    [Fact]
    public void Nvme_bridge_commands_ask_for_the_whole_health_log()
    {
        Assert.Equal(0x007F0002u, NvmeBridge.LogDword10(NvmeBridge.HealthLog, 512));

        byte[] asmedia = NvmeBridge.HealthSteps(BridgeKind.AsmediaNvme).Single().Cdb;
        Assert.Equal([0xE6, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, 0x7F], asmedia[..8]);

        BridgeStep realtek = NvmeBridge.HealthSteps(BridgeKind.RealtekNvme).Single();
        Assert.Equal([0xE4, 0x00, 0x02, 0x02, 0x02], realtek.Cdb[..5]);
        Assert.Equal(ScsiDirection.In, realtek.Direction);

        Assert.Empty(NvmeBridge.HealthSteps(BridgeKind.Sata));
        Assert.Empty(NvmeBridge.HealthSteps(BridgeKind.Unknown));
    }

    [Fact]
    public void The_jmicron_tunnel_sends_the_command_then_reads_the_data_and_the_reply()
    {
        IReadOnlyList<BridgeStep> steps = NvmeBridge.HealthSteps(BridgeKind.JMicronNvme);

        Assert.Equal(3, steps.Count);
        Assert.Equal([ScsiDirection.Out, ScsiDirection.In, ScsiDirection.In], steps.Select(s => s.Direction));
        Assert.Equal([0x80, 0x82, 0x8F], steps.Select(s => s.Cdb[1]));
        Assert.All(steps, s => Assert.Equal(0xA1, s.Cdb[0]));
        Assert.Equal(512, BinaryPrimitives.ReadUInt16BigEndian(steps[1].Cdb.AsSpan(3)));
        byte[] command = steps[0].Payload;
        Assert.Equal(NvmeBridge.JMicronSignature, BinaryPrimitives.ReadUInt32LittleEndian(command));
        Assert.Equal(NvmeBridge.GetLogPage, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(8)));
        Assert.Equal(NvmeBridge.AllNamespaces, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(12)));
        Assert.Equal(0x007F0002u, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(40)));
        Assert.False(steps[2].Required);
    }

    [Fact]
    public void A_sat_bridge_reports_temperature_hours_sectors_and_thresholds()
    {
        var bridge = new SatBridge
        {
            Identify = IdentifySector("WDC WD50NDZW-11BCSS0", 4800, smart: true, trim: false),
            Smart = SmartSector((1, 184, 78, 1840), (5, 200, 200, 2), (9, 100, 100, 427), (194, 115, 98, 37), (197, 200, 200, 337), (198, 100, 253, 0)),
            Thresholds = ThresholdSector((1, 51), (5, 140), (9, 0), (194, 0), (197, 0), (198, 0)),
        };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(), bridge);

        Assert.Equal(HealthSource.SatPassThrough, health.Source);
        Assert.Equal(HealthProtocol.Sata, health.Protocol);
        Assert.True(health.Bridged);
        Assert.Equal("Western Digital (1058:2626)", health.Bridge);
        Assert.Equal("WDC WD50NDZW-11BCSS0", health.Model);
        Assert.Equal(37, health.TemperatureCelsius);
        Assert.Equal(427, health.PowerOnHours);
        Assert.Equal((2L, 337L, 0L), (health.ReallocatedSectors, health.PendingSectors, health.UncorrectableSectors));
        Assert.Equal(HealthWarning.ReallocatedSectors | HealthWarning.PendingSectors, health.Warnings);
        Assert.Equal(HealthStatus.Warning, health.Status);
        Assert.Equal(6, health.Table.Count);
        Assert.Equal(51, health.Table.Single(a => a.Id == 1).Threshold);
        Assert.Equal(78, health.Table.Single(a => a.Id == 1).Worst);
        Assert.All(bridge.Cdbs, c => Assert.Equal(0x85, c[0]));
    }

    [Fact]
    public void A_bridge_that_refuses_the_sixteen_byte_form_is_asked_with_twelve_bytes()
    {
        var bridge = new SatBridge
        {
            Rejects16 = true,
            Identify = IdentifySector("Portable SSD T7", MediaClassifier.NonRotating, smart: true, trim: true),
            Smart = SmartSector((9, 100, 100, 50), (231, 92, 92, 0), (194, 60, 50, 31)),
            Thresholds = ThresholdSector((9, 0), (231, 10)),
        };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(MediaKind.Unknown), bridge);

        Assert.Equal(HealthSource.SatPassThrough, health.Source);
        Assert.Equal(8, health.LifeUsedPercent);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(0x85, bridge.Cdbs[0][0]);
        Assert.Contains(bridge.Cdbs, c => c[0] == 0xA1 && c[9] == 0xB0 && c[3] == 0xD0);
    }

    [Fact]
    public void An_attribute_at_its_threshold_raises_a_warning()
    {
        var bridge = new SatBridge
        {
            Identify = IdentifySector("Old Disk", 7200, smart: true, trim: false),
            Smart = SmartSector((5, 140, 140, 900), (9, 100, 100, 30_000)),
            Thresholds = ThresholdSector((5, 140), (9, 0)),
        };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(), bridge);

        Assert.True(health.Warnings.HasFlag(HealthWarning.AttributeFailing));
        Assert.True(health.Table.Single(a => a.Id == 5).Failing);
        Assert.False(health.Table.Single(a => a.Id == 9).Failing);
    }

    [Fact]
    public void Smart_is_not_read_when_identify_says_the_feature_is_missing()
    {
        var bridge = new SatBridge { Identify = IdentifySector("No Smart", 7200, smart: false, trim: false), Smart = SmartSector((9, 100, 100, 1)) };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(), bridge);

        Assert.False(health.HasData);
        Assert.Equal("No Smart", health.Model);
        Assert.True(health.Bridged);
        Assert.DoesNotContain(bridge.Cdbs, c => c[14] == 0xB0);
    }

    [Theory]
    [InlineData(JMicronNvme)]
    [InlineData(AsmediaNvme)]
    [InlineData(RealtekNvme)]
    public void Nvme_bridges_hand_over_the_health_log_through_their_own_commands(string id)
    {
        var bridge = new NvmeTunnel(id, NvmeLog(kelvin: 318, spare: 100, threshold: 10, used: 3, hours: 1_234));

        DriveHealth health = DriveHealthReader.Read(UsbVolume(MediaKind.Ssd), bridge);

        Assert.Equal(HealthSource.NvmeBridge, health.Source);
        Assert.Equal(HealthProtocol.Nvme, health.Protocol);
        Assert.Equal(45, health.TemperatureCelsius);
        Assert.Equal(1_234, health.PowerOnHours);
        Assert.Equal(3, health.LifeUsedPercent);
        Assert.NotNull(health.Counters);
        Assert.Equal(77, health.Counters.UnsafeShutdowns);
        Assert.DoesNotContain(bridge.Cdbs, c => c[0] == 0x85);
        Assert.True(health.Bridged);
    }

    [Fact]
    public void A_jmicron_tunnel_without_the_signature_payload_gives_nothing()
    {
        var bridge = new NvmeTunnel(JMicronNvme, NvmeLog(318, 100, 10, 3, 5)) { CheckSignature = true, BreakSignature = true };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(MediaKind.Ssd), bridge);

        Assert.False(health.HasData);
        Assert.True(health.Bridged);
    }

    [Fact]
    public void Without_pass_through_rights_the_reader_falls_back_quietly()
    {
        var bridge = new SatBridge { Denied = true };

        DriveHealth health = DriveHealthReader.Read(UsbVolume(), bridge);

        Assert.False(health.HasData);
        Assert.True(health.Bridged);
        Assert.Single(bridge.Cdbs);
    }

    [Fact]
    public void Nvme_counters_come_from_the_health_log()
    {
        byte[] log = NvmeLog(300, 100, 10, 1, 10);
        DriveHealth? health = SmartParser.FromNvme(log);

        Assert.NotNull(health?.Counters);
        Assert.Equal(new NvmeCounters(1_000, 2_000, 35, 77, 0, 4), health.Counters);
        Assert.Equal(512_000, NvmeCounters.BytesPerDataUnit);
    }

    internal static byte[] SmartSector(params (byte Id, byte Current, byte Worst, long Raw)[] attributes)
    {
        byte[] sector = new byte[512];
        sector[0] = 0x10;
        for (int i = 0; i < attributes.Length; i++)
        {
            int offset = 2 + (i * 12);
            sector[offset] = attributes[i].Id;
            sector[offset + 3] = attributes[i].Current;
            sector[offset + 4] = attributes[i].Worst;
            for (int b = 0; b < 6; b++)
            {
                sector[offset + 5 + b] = (byte)(attributes[i].Raw >> (8 * b));
            }
        }

        Seal(sector);
        return sector;
    }

    internal static byte[] ThresholdSector(params (byte Id, byte Threshold)[] thresholds)
    {
        byte[] sector = new byte[512];
        sector[0] = 0x10;
        for (int i = 0; i < thresholds.Length; i++)
        {
            sector[2 + (i * 12)] = thresholds[i].Id;
            sector[3 + (i * 12)] = thresholds[i].Threshold;
        }

        Seal(sector);
        return sector;
    }

    internal static byte[] IdentifySector(string model, int rotation, bool smart, bool trim)
    {
        byte[] sector = new byte[512];
        Text(sector, 10, 10, "SN123");
        Text(sector, 23, 4, "01.01A01");
        Text(sector, 27, 20, model);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(82 * 2), (ushort)(smart ? 0x4001 : 0x4000));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(85 * 2), (ushort)(smart ? 0x4001 : 0x4000));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(169 * 2), (ushort)(trim ? 1 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(217 * 2), (ushort)rotation);
        sector[510] = AtaIdentity.IntegritySignature;
        Seal(sector);
        return sector;
    }

    internal static byte[] NvmeLog(int kelvin, byte spare, byte threshold, byte used, long hours)
    {
        byte[] log = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), (ushort)kelvin);
        log[3] = spare;
        log[4] = threshold;
        log[5] = used;
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(32), 1_000);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(48), 2_000);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(112), 35);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(128), hours);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(144), 77);
        BinaryPrimitives.WriteInt64LittleEndian(log.AsSpan(176), 4);
        return log;
    }

    private static void Text(byte[] sector, int word, int words, string value)
    {
        string padded = value.PadRight(words * 2);
        for (int i = 0; i < words; i++)
        {
            sector[(word + i) * 2] = (byte)padded[(i * 2) + 1];
            sector[((word + i) * 2) + 1] = (byte)padded[i * 2];
        }
    }

    private static void Seal(byte[] sector)
    {
        sector[511] = 0;
        byte sum = 0;
        foreach (byte value in sector)
        {
            sum = unchecked((byte)(sum + value));
        }

        sector[511] = unchecked((byte)(0x100 - sum));
    }

    private static ScsiReply Good(int length) => new(ScsiSense.GoodStatus, length, new byte[VolumeNative.CommandSenseBytes]);

    private static ScsiReply Illegal() => new(ScsiSense.CheckCondition, 0, [0x70, 0, 0x05, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0x20, 0x00]);

    private sealed class SatBridge : IHealthProbe
    {
        public bool Rejects16 { get; init; }

        public bool Denied { get; init; }

        public byte[]? Identify { get; init; }

        public byte[]? Smart { get; init; }

        public byte[]? Thresholds { get; init; }

        public List<byte[]> Cdbs { get; } = [];

        public string? UsbParent => SataBridge;

        public int ReadNvmeLog(Span<byte> log) => NotSupported;

        public int PredictFailure(out bool predicted, Span<byte> data)
        {
            predicted = false;
            return NotSupported;
        }

        public int ReadTemperature(out TemperatureFacts facts)
        {
            facts = default;
            return NotSupported;
        }

        public int Command(ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
        {
            Cdbs.Add(cdb.ToArray());
            reply = Good(0);
            if (Denied)
            {
                return VolumeNative.ErrorAccessDenied;
            }

            bool sixteen = cdb[0] == SatCommand.PassThrough16;
            if (sixteen && Rejects16)
            {
                reply = Illegal();
                return 0;
            }

            byte command = sixteen ? cdb[14] : cdb[9];
            byte feature = sixteen ? cdb[4] : cdb[3];
            byte[]? answer = command == SatCommand.IdentifyCommand ? Identify : feature == SatCommand.SmartReadData ? Smart : Thresholds;
            if (answer is null)
            {
                reply = Illegal();
                return 0;
            }

            answer.CopyTo(data);
            reply = Good(answer.Length);
            return 0;
        }
    }

    private sealed class NvmeTunnel(string id, byte[] log) : IHealthProbe
    {
        private bool _armed;

        public bool CheckSignature { get; init; } = true;

        public bool BreakSignature { get; init; }

        public List<byte[]> Cdbs { get; } = [];

        public string? UsbParent => id;

        public int ReadNvmeLog(Span<byte> buffer) => NotSupported;

        public int PredictFailure(out bool predicted, Span<byte> data)
        {
            predicted = false;
            return NotSupported;
        }

        public int ReadTemperature(out TemperatureFacts facts)
        {
            facts = default;
            return NotSupported;
        }

        public int Command(ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
        {
            Cdbs.Add(cdb.ToArray());
            reply = Good(data.Length);
            switch (cdb[0])
            {
                case NvmeBridge.AsmediaOpcode:
                case NvmeBridge.RealtekOpcode:
                    log.CopyTo(data);
                    return 0;
                case SatCommand.PassThrough12 when (cdb[1] & 0x0F) == NvmeBridge.JMicronCommand && direction == ScsiDirection.Out:
                    uint signature = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    _armed = !CheckSignature || (signature == NvmeBridge.JMicronSignature && !BreakSignature);
                    reply = _armed ? Good(data.Length) : Illegal();
                    return 0;
                case SatCommand.PassThrough12 when (cdb[1] & 0x0F) == NvmeBridge.JMicronDataIn:
                    if (!_armed)
                    {
                        reply = Illegal();
                        return 0;
                    }

                    log.CopyTo(data);
                    return 0;
                case SatCommand.PassThrough12 when (cdb[1] & 0x0F) == NvmeBridge.JMicronResponse:
                    BinaryPrimitives.WriteUInt32LittleEndian(data, NvmeBridge.JMicronSignature);
                    return 0;
                default:
                    reply = Illegal();
                    return 0;
            }
        }
    }
}
