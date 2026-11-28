using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public enum SenseKey : byte
{
    NoSense = 0,
    Recovered = 1,
    NotReady = 2,
    MediumError = 3,
    HardwareError = 4,
    IllegalRequest = 5,
    UnitAttention = 6,
    Aborted = 11,
}

public readonly record struct ScsiSense(SenseKey Key, byte Asc, byte Ascq)
{
    public const byte GoodStatus = 0;
    public const byte CheckCondition = 2;

    private const byte FixedCurrent = 0x70;
    private const byte FixedDeferred = 0x71;
    private const byte DescriptorCurrent = 0x72;
    private const byte DescriptorDeferred = 0x73;
    private const byte ResponseMask = 0x7F;
    private const byte KeyMask = 0x0F;
    private const int FixedKey = 2;
    private const int FixedAsc = 12;
    private const int FixedAscq = 13;
    private const int DescriptorKey = 1;
    private const int DescriptorAsc = 2;
    private const int DescriptorAscq = 3;

    public static ScsiSense? Parse(ReadOnlySpan<byte> sense)
    {
        if (sense.IsEmpty)
        {
            return null;
        }

        byte response = (byte)(sense[0] & ResponseMask);
        if (response is FixedCurrent or FixedDeferred && sense.Length > FixedAscq)
        {
            return new ScsiSense((SenseKey)(sense[FixedKey] & KeyMask), sense[FixedAsc], sense[FixedAscq]);
        }

        return response is DescriptorCurrent or DescriptorDeferred && sense.Length > DescriptorAscq
            ? new ScsiSense((SenseKey)(sense[DescriptorKey] & KeyMask), sense[DescriptorAsc], sense[DescriptorAscq])
            : null;
    }

    public static bool Succeeded(ScsiReply reply)
    {
        if (reply.Status == GoodStatus)
        {
            return true;
        }

        return reply.Status == CheckCondition && Parse(reply.Sense) is { Key: SenseKey.NoSense or SenseKey.Recovered };
    }

    public static bool Rejected(ScsiReply reply) =>
        reply.Status == CheckCondition && Parse(reply.Sense) is { Key: SenseKey.IllegalRequest };
}

public static class SatCommand
{
    public const byte PassThrough16 = 0x85;
    public const byte PassThrough12 = 0xA1;
    public const byte SmartCommand = 0xB0;
    public const byte IdentifyCommand = 0xEC;
    public const byte SmartReadData = 0xD0;
    public const byte SmartReadThresholds = 0xD1;
    public const byte SmartCylinderLow = 0x4F;
    public const byte SmartCylinderHigh = 0xC2;
    public const byte DeviceRegister = 0xA0;
    public const byte PioDataIn = 4;
    public const byte DataInFlags = 0x0E;
    public const int SectorBytes = 512;
    public const int Length16 = 16;
    public const int Length12 = 12;

    private const int ProtocolShift = 1;

    public static byte[] Build(bool sixteen, byte command, byte feature, byte count, byte lbaLow, byte lbaMid, byte lbaHigh)
    {
        byte protocol = (byte)(PioDataIn << ProtocolShift);
        if (sixteen)
        {
            byte[] cdb = new byte[Length16];
            cdb[0] = PassThrough16;
            cdb[1] = protocol;
            cdb[2] = DataInFlags;
            cdb[4] = feature;
            cdb[6] = count;
            cdb[8] = lbaLow;
            cdb[10] = lbaMid;
            cdb[12] = lbaHigh;
            cdb[13] = DeviceRegister;
            cdb[14] = command;
            return cdb;
        }

        byte[] shortCdb = new byte[Length12];
        shortCdb[0] = PassThrough12;
        shortCdb[1] = protocol;
        shortCdb[2] = DataInFlags;
        shortCdb[3] = feature;
        shortCdb[4] = count;
        shortCdb[5] = lbaLow;
        shortCdb[6] = lbaMid;
        shortCdb[7] = lbaHigh;
        shortCdb[8] = DeviceRegister;
        shortCdb[9] = command;
        return shortCdb;
    }

    public static byte[] SmartRead(bool sixteen, byte feature) => Build(sixteen, SmartCommand, feature, 1, 0, SmartCylinderLow, SmartCylinderHigh);

    public static byte[] Identify(bool sixteen) => Build(sixteen, IdentifyCommand, 0, 1, 0, 0, 0);

    public static bool ChecksumMatches(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SectorBytes)
        {
            return false;
        }

        byte sum = 0;
        foreach (byte value in sector[..SectorBytes])
        {
            sum = unchecked((byte)(sum + value));
        }

        return sum == 0;
    }

    public static bool LooksLikeSmartData(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SectorBytes || sector[..SectorBytes].IndexOfAnyExcept((byte)0) < 0 || sector[..SectorBytes].IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return false;
        }

        if (ChecksumMatches(sector))
        {
            return true;
        }

        byte first = sector[SmartParser.AttributeTableOffset];
        byte current = sector[SmartParser.AttributeTableOffset + 3];
        return first != 0 && current is > 0 and < byte.MaxValue;
    }
}

public sealed record AtaIdentity(string Model, string Serial, string Firmware, int RotationRate, bool SmartSupported, bool SmartEnabled, bool TrimSupported)
{
    public const byte IntegritySignature = 0xA5;

    private const int SerialWord = 10;
    private const int SerialWords = 10;
    private const int FirmwareWord = 23;
    private const int FirmwareWords = 4;
    private const int ModelWord = 27;
    private const int ModelWords = 20;
    private const int CommandSetWord = 82;
    private const int EnabledSetWord = 85;
    private const int DataSetWord = 169;
    private const int RotationWord = 217;
    private const int IntegrityWord = 255;
    private const ushort SmartBit = 0x0001;
    private const ushort TrimBit = 0x0001;
    private const ushort Unset = 0xFFFF;

    public bool IsSolidState => RotationRate == MediaClassifier.NonRotating;

    public static AtaIdentity? Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SatCommand.SectorBytes || sector[..SatCommand.SectorBytes].IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        if (sector[IntegrityWord * 2] == IntegritySignature && !SatCommand.ChecksumMatches(sector))
        {
            return null;
        }

        string model = Text(sector, ModelWord, ModelWords);
        if (model.Length == 0)
        {
            return null;
        }

        ushort commands = Word(sector, CommandSetWord);
        ushort enabled = Word(sector, EnabledSetWord);
        ushort dataSet = Word(sector, DataSetWord);
        bool known = commands != Unset;
        return new AtaIdentity(
            model,
            Text(sector, SerialWord, SerialWords),
            Text(sector, FirmwareWord, FirmwareWords),
            Word(sector, RotationWord),
            known && (commands & SmartBit) != 0,
            enabled != Unset && (enabled & SmartBit) != 0,
            dataSet != Unset && (dataSet & TrimBit) != 0);
    }

    private static ushort Word(ReadOnlySpan<byte> sector, int word) => BinaryPrimitives.ReadUInt16LittleEndian(sector[(word * 2)..]);

    private static string Text(ReadOnlySpan<byte> sector, int word, int words)
    {
        var text = new StringBuilder(words * 2);
        for (int i = 0; i < words; i++)
        {
            ushort value = Word(sector, word + i);
            Append(text, (byte)(value >> 8));
            Append(text, (byte)value);
        }

        return text.ToString().Trim();
    }

    private static void Append(StringBuilder text, byte value) => text.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
}

public enum BridgeKind
{
    Unknown,
    Sata,
    JMicronNvme,
    AsmediaNvme,
    RealtekNvme,
}

public readonly record struct UsbBridge(ushort Vendor, ushort Product)
{
    public const ushort JMicron = 0x152D;
    public const ushort Asmedia = 0x174C;
    public const ushort Realtek = 0x0BDA;
    public const ushort Initio = 0x13FD;
    public const ushort WesternDigital = 0x1058;
    public const ushort Cypress = 0x04B4;
    public const ushort Seagate = 0x0BC2;
    public const ushort Toshiba = 0x0480;

    private const string VendorTag = "VID_";
    private const string ProductTag = "PID_";
    private const int HexDigits = 4;

    private static readonly ushort[] JMicronNvme = [0x0583];
    private static readonly ushort[] AsmediaNvme = [0x2362, 0x2364];
    private static readonly ushort[] RealtekNvme = [0x9210];
    private static readonly ushort[] SataVendors = [JMicron, Asmedia, Initio, WesternDigital, Cypress, Seagate, Toshiba];

    public BridgeKind Kind => Vendor switch
    {
        JMicron when JMicronNvme.Contains(Product) => BridgeKind.JMicronNvme,
        Asmedia when AsmediaNvme.Contains(Product) => BridgeKind.AsmediaNvme,
        Realtek when RealtekNvme.Contains(Product) => BridgeKind.RealtekNvme,
        _ when SataVendors.Contains(Vendor) => BridgeKind.Sata,
        _ => BridgeKind.Unknown,
    };

    public bool CarriesNvme => Kind is BridgeKind.JMicronNvme or BridgeKind.AsmediaNvme or BridgeKind.RealtekNvme;

    public string VendorName => Vendor switch
    {
        JMicron => "JMicron",
        Asmedia => "ASMedia",
        Realtek => "Realtek",
        Initio => "Initio",
        WesternDigital => "Western Digital",
        Cypress => "Cypress",
        Seagate => "Seagate",
        Toshiba => "Toshiba",
        _ => string.Create(CultureInfo.InvariantCulture, $"{Vendor:X4}"),
    };

    public string Name => string.Create(CultureInfo.InvariantCulture, $"{VendorName} ({Vendor:X4}:{Product:X4})");

    public static UsbBridge? Parse(string? hardwareId)
    {
        if (string.IsNullOrEmpty(hardwareId))
        {
            return null;
        }

        int vendor = hardwareId.IndexOf(VendorTag, StringComparison.OrdinalIgnoreCase);
        int product = hardwareId.IndexOf(ProductTag, StringComparison.OrdinalIgnoreCase);
        if (vendor < 0 || product < 0 || vendor + VendorTag.Length + HexDigits > hardwareId.Length || product + ProductTag.Length + HexDigits > hardwareId.Length)
        {
            return null;
        }

        return ushort.TryParse(hardwareId.AsSpan(vendor + VendorTag.Length, HexDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort vid)
            && ushort.TryParse(hardwareId.AsSpan(product + ProductTag.Length, HexDigits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort pid)
            ? new UsbBridge(vid, pid)
            : null;
    }
}

public readonly record struct BridgeStep(byte[] Cdb, ScsiDirection Direction, byte[] Payload, int Length, bool Required);

public static class NvmeBridge
{
    public const byte GetLogPage = 0x02;
    public const byte HealthLog = 0x02;
    public const uint AllNamespaces = 0xFFFFFFFF;
    public const byte AsmediaOpcode = 0xE6;
    public const byte RealtekOpcode = 0xE4;
    public const uint JMicronSignature = 0x454D564E;
    public const byte JMicronAdmin = 0x80;
    public const byte JMicronCommand = 0x00;
    public const byte JMicronDataIn = 0x02;
    public const byte JMicronResponse = 0x0F;
    public const int JMicronCommandBytes = 512;
    public const int JMicronReplyBytes = 24;

    private const int AsmediaLength = 16;
    private const int RealtekLength = 16;
    private const int JMicronLength = 12;
    private const int DwordBytes = 4;
    private const int OpcodeDword = 2;
    private const int NamespaceDword = 3;
    private const int Dword10 = 10;
    private const int NumberShift = 16;

    public static uint LogDword10(byte logId, int bytes) => logId | ((uint)((bytes / DwordBytes) - 1) << NumberShift);

    public static IReadOnlyList<BridgeStep> HealthSteps(BridgeKind kind, int bytes = VolumeNative.HealthLogBytes)
    {
        uint cdw10 = LogDword10(HealthLog, bytes);
        return kind switch
        {
            BridgeKind.AsmediaNvme => [new BridgeStep(Asmedia(GetLogPage, cdw10), ScsiDirection.In, [], bytes, Required: true)],
            BridgeKind.RealtekNvme => [new BridgeStep(Realtek(GetLogPage, cdw10, bytes), ScsiDirection.In, [], bytes, Required: true)],
            BridgeKind.JMicronNvme => JMicron(GetLogPage, AllNamespaces, cdw10, bytes),
            _ => [],
        };
    }

    public static byte[] Asmedia(byte opcode, uint cdw10)
    {
        byte[] cdb = new byte[AsmediaLength];
        cdb[0] = AsmediaOpcode;
        cdb[1] = opcode;
        cdb[3] = (byte)cdw10;
        cdb[7] = (byte)(cdw10 >> NumberShift);
        return cdb;
    }

    public static byte[] Realtek(byte opcode, uint cdw10, int bytes)
    {
        byte[] cdb = new byte[RealtekLength];
        cdb[0] = RealtekOpcode;
        BinaryPrimitives.WriteUInt16LittleEndian(cdb.AsSpan(1), (ushort)bytes);
        cdb[3] = opcode;
        cdb[4] = (byte)cdw10;
        return cdb;
    }

    public static IReadOnlyList<BridgeStep> JMicron(byte opcode, uint nsid, uint cdw10, int bytes)
    {
        byte[] command = new byte[JMicronCommandBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(command, JMicronSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(OpcodeDword * DwordBytes), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(NamespaceDword * DwordBytes), nsid);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(Dword10 * DwordBytes), cdw10);
        return
        [
            new BridgeStep(JMicronCdb(JMicronCommand, JMicronCommandBytes), ScsiDirection.Out, command, JMicronCommandBytes, Required: true),
            new BridgeStep(JMicronCdb(JMicronDataIn, bytes), ScsiDirection.In, [], bytes, Required: true),
            new BridgeStep(JMicronCdb(JMicronResponse, JMicronReplyBytes), ScsiDirection.In, [], JMicronReplyBytes, Required: false),
        ];
    }

    private static byte[] JMicronCdb(byte protocol, int bytes)
    {
        byte[] cdb = new byte[JMicronLength];
        cdb[0] = SatCommand.PassThrough12;
        cdb[1] = (byte)(JMicronAdmin | protocol);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(3), (ushort)bytes);
        return cdb;
    }
}

public sealed record SatResult(AtaIdentity? Identity, byte[]? Smart, byte[]? Thresholds, bool Sixteen);

public static class BridgeReader
{
    public static SatResult? ReadSat(IHealthProbe probe, bool smart = true)
    {
        ArgumentNullException.ThrowIfNull(probe);
        foreach (bool sixteen in (bool[])[true, false])
        {
            byte[] sector = new byte[SatCommand.SectorBytes];
            if (!Run(probe, SatCommand.Identify(sixteen), sector, out bool rejected))
            {
                if (rejected)
                {
                    continue;
                }

                return null;
            }

            AtaIdentity? identity = AtaIdentity.Parse(sector);
            if (identity is null)
            {
                continue;
            }

            if (!smart || (!identity.SmartSupported && !identity.SmartEnabled))
            {
                return new SatResult(identity, null, null, sixteen);
            }

            byte[] data = new byte[SatCommand.SectorBytes];
            if (!Run(probe, SatCommand.SmartRead(sixteen, SatCommand.SmartReadData), data, out _) || !SatCommand.LooksLikeSmartData(data))
            {
                return new SatResult(identity, null, null, sixteen);
            }

            byte[] thresholds = new byte[SatCommand.SectorBytes];
            bool haveThresholds = Run(probe, SatCommand.SmartRead(sixteen, SatCommand.SmartReadThresholds), thresholds, out _) && SatCommand.LooksLikeSmartData(thresholds);
            return new SatResult(identity, data, haveThresholds ? thresholds : null, sixteen);
        }

        return null;
    }

    public static bool ReadNvmeHealth(IHealthProbe probe, BridgeKind kind, Span<byte> log)
    {
        ArgumentNullException.ThrowIfNull(probe);
        IReadOnlyList<BridgeStep> steps = NvmeBridge.HealthSteps(kind, log.Length);
        if (steps.Count == 0)
        {
            return false;
        }

        bool data = false;
        foreach (BridgeStep step in steps)
        {
            byte[] buffer = step.Direction == ScsiDirection.Out ? step.Payload : new byte[step.Length];
            bool ok = probe.Command(step.Cdb, step.Direction, buffer, out ScsiReply reply) == 0 && ScsiSense.Succeeded(reply);
            if (!ok && step.Required)
            {
                return false;
            }

            if (ok && step.Direction == ScsiDirection.In && step.Length == log.Length && !data)
            {
                buffer.AsSpan(0, Math.Min(reply.Transferred == 0 ? buffer.Length : reply.Transferred, log.Length)).CopyTo(log);
                data = true;
            }
        }

        return data && log.IndexOfAnyExcept((byte)0) >= 0;
    }

    private static bool Run(IHealthProbe probe, byte[] cdb, byte[] sector, out bool rejected)
    {
        rejected = false;
        int error = probe.Command(cdb, ScsiDirection.In, sector, out ScsiReply reply);
        if (error != 0)
        {
            rejected = error is VolumeNative.ErrorNotSupported or VolumeNative.ErrorInvalidParameter;
            return false;
        }

        if (ScsiSense.Succeeded(reply))
        {
            return true;
        }

        rejected = ScsiSense.Rejected(reply);
        return false;
    }
}
