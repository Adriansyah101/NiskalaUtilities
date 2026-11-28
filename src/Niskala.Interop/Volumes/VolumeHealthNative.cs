using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Niskala.Interop.Volumes;

public readonly record struct TemperatureFacts(int Current, int Warning, int Critical);

public static partial class VolumeNative
{
    public const int HealthLogBytes = 512;
    public const int FailureDataBytes = 512;

    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlStoragePredictFailure = 0x002D1100;
    private const int StorageAdapterProtocolSpecificProperty = 49;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int StorageDeviceTemperatureProperty = 52;
    private const int ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    private const uint NvmeLogPageHealthInfo = 2;
    private const int QueryHeaderBytes = 8;
    private const int ProtocolDataBytes = 40;
    private const int ProtocolDataOffsetField = 16;
    private const int ProtocolDataLengthField = 20;
    private const int ProtocolBufferBytes = QueryHeaderBytes + ProtocolDataBytes + HealthLogBytes;
    private const int TemperatureBufferBytes = 256;
    private const int TemperatureInfoOffset = 24;
    private const int TemperatureInfoBytes = 16;
    private const string PhysicalDrivePrefix = @"\\.\PhysicalDrive";

    public static int TryOpenDisk(char letter, out SafeFileHandle disk)
    {
        disk = CreateFile($@"\\.\{letter}:", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (disk.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        if (TryGetDeviceNumber(disk, out uint number) != 0)
        {
            return 0;
        }

        SafeFileHandle physical = CreateFile(PhysicalDrivePrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (physical.IsInvalid)
        {
            physical.Dispose();
            return 0;
        }

        disk.Dispose();
        disk = physical;
        return 0;
    }

    public static unsafe int TryGetDeviceNumber(SafeFileHandle device, out uint number)
    {
        number = 0;
        DeviceNumber result;
        if (!DeviceIoControl(device, IoctlStorageGetDeviceNumber, null, 0, &result, (uint)sizeof(DeviceNumber), out uint returned, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (returned < sizeof(DeviceNumber))
        {
            return ErrorInvalidData;
        }

        number = result.Number;
        return 0;
    }

    public static int TryReadNvmeHealth(SafeFileHandle disk, Span<byte> log)
    {
        int error = TryReadNvmeHealth(disk, StorageDeviceProtocolSpecificProperty, log);
        return error == 0 ? 0 : TryReadNvmeHealth(disk, StorageAdapterProtocolSpecificProperty, log);
    }

    public static unsafe int TryPredictFailure(SafeFileHandle disk, out bool predicted, Span<byte> data)
    {
        predicted = false;
        PredictFailure result;
        if (!DeviceIoControl(disk, IoctlStoragePredictFailure, null, 0, &result, (uint)sizeof(PredictFailure), out uint returned, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (returned < sizeof(uint))
        {
            return ErrorInvalidData;
        }

        predicted = result.Predicted != 0;
        new ReadOnlySpan<byte>(result.VendorSpecific, FailureDataBytes)[..Math.Min(data.Length, FailureDataBytes)].CopyTo(data);
        return 0;
    }

    public static unsafe int TryReadTemperature(SafeFileHandle disk, out TemperatureFacts facts)
    {
        facts = default;
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceTemperatureProperty, QueryType = PropertyStandardQuery };
        byte* buffer = stackalloc byte[TemperatureBufferBytes];
        if (!DeviceIoControl(disk, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), buffer, TemperatureBufferBytes, out uint returned, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (returned < TemperatureInfoOffset + TemperatureInfoBytes)
        {
            return ErrorInvalidData;
        }

        var descriptor = (TemperatureDescriptor*)buffer;
        if (descriptor->InfoCount == 0)
        {
            return ErrorNotSupported;
        }

        var info = (TemperatureInfo*)(buffer + TemperatureInfoOffset);
        facts = new TemperatureFacts(info->Temperature, descriptor->WarningTemperature, descriptor->CriticalTemperature);
        return 0;
    }

    private static unsafe int TryReadNvmeHealth(SafeFileHandle disk, int property, Span<byte> log)
    {
        byte* buffer = stackalloc byte[ProtocolBufferBytes];
        new Span<byte>(buffer, ProtocolBufferBytes).Clear();
        var query = (ProtocolQuery*)buffer;
        query->PropertyId = property;
        query->QueryType = PropertyStandardQuery;
        query->ProtocolType = ProtocolTypeNvme;
        query->DataType = NvmeDataTypeLogPage;
        query->RequestValue = NvmeLogPageHealthInfo;
        query->DataOffset = ProtocolDataBytes;
        query->DataLength = HealthLogBytes;
        if (!DeviceIoControl(disk, IoctlStorageQueryProperty, buffer, ProtocolBufferBytes, buffer, ProtocolBufferBytes, out uint returned, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        var answer = new ReadOnlySpan<byte>(buffer, (int)Math.Min(returned, (uint)ProtocolBufferBytes));
        if (answer.Length < QueryHeaderBytes + ProtocolDataBytes)
        {
            return ErrorInvalidData;
        }

        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(answer[(QueryHeaderBytes + ProtocolDataOffsetField)..]);
        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(answer[(QueryHeaderBytes + ProtocolDataLengthField)..]);
        long start = QueryHeaderBytes + (long)offset;
        int take = (int)Math.Min(Math.Min(length, (uint)HealthLogBytes), (uint)log.Length);
        if (offset < ProtocolDataBytes || take <= 0 || start + take > answer.Length)
        {
            return ErrorInvalidData;
        }

        answer.Slice((int)start, take).CopyTo(log);
        return 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct DeviceNumber
    {
        [FieldOffset(4)]
        public uint Number;
    }

    [StructLayout(LayoutKind.Explicit, Size = 516)]
    private unsafe struct PredictFailure
    {
        [FieldOffset(0)]
        public uint Predicted;

        [FieldOffset(4)]
        public fixed byte VendorSpecific[FailureDataBytes];
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ProtocolQuery
    {
        [FieldOffset(0)]
        public int PropertyId;

        [FieldOffset(4)]
        public int QueryType;

        [FieldOffset(8)]
        public int ProtocolType;

        [FieldOffset(12)]
        public uint DataType;

        [FieldOffset(16)]
        public uint RequestValue;

        [FieldOffset(20)]
        public uint RequestSubValue;

        [FieldOffset(24)]
        public uint DataOffset;

        [FieldOffset(28)]
        public uint DataLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct TemperatureDescriptor
    {
        [FieldOffset(8)]
        public short CriticalTemperature;

        [FieldOffset(10)]
        public short WarningTemperature;

        [FieldOffset(12)]
        public ushort InfoCount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct TemperatureInfo
    {
        [FieldOffset(2)]
        public short Temperature;
    }
}
