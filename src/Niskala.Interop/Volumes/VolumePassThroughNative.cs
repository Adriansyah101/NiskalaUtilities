using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Niskala.Interop.Volumes;

public enum ScsiDirection : byte
{
    Out = 0,
    In = 1,
    None = 2,
}

public readonly record struct ScsiReply(byte Status, int Transferred, byte[] Sense);

public static partial class VolumeNative
{
    public const int CommandDataBytes = 512;
    public const int CommandSenseBytes = 32;
    public const int MaxCdbBytes = 16;

    private const uint IoctlScsiPassThroughDirect = 0x0004D014;
    private const int ErrorInvalidFunction = 1;
    private const uint CommandTimeoutSeconds = 3;
    private const int CommandDataOffset = 96;
    private const int DirectHeaderBytes = 56;
    private const int DirectSenseOffset = 56;
    private const int DirectBufferBytes = 96;
    private const uint ConfigSuccess = 0;
    private const uint InterfaceListPresent = 0;
    private const uint InstanceIdProperty = 256;
    private const int DeviceIdChars = 200;
    private const int InstanceIdBytes = 512;
    private const int MaxParents = 8;
    private const string UsbPrefix = @"USB\VID_";

    private static readonly Guid DiskInterface = new(0x53f56307, 0xb6bf, 0x11d0, 0x94, 0xf2, 0x00, 0xa0, 0xc9, 0x1e, 0xfb, 0x8b);
    private static readonly Guid InstanceIdKey = new(0x78c34fc8, 0x104a, 0x4aca, 0x9e, 0xa4, 0x52, 0x4d, 0x52, 0x99, 0x6e, 0x57);

    public static int TryGetDiskNumber(char letter, out uint number)
    {
        number = 0;
        using SafeFileHandle volume = CreateFile($@"\\.\{letter}:", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        return volume.IsInvalid ? Marshal.GetLastPInvokeError() : TryGetDeviceNumber(volume, out number);
    }

    public static int TryOpenDiskForCommands(char letter, out SafeFileHandle disk)
    {
        int error = TryGetDiskNumber(letter, out uint number);
        if (error != 0)
        {
            disk = new SafeFileHandle();
            return error;
        }

        disk = CreateFile(PhysicalDrivePrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture), GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (!disk.IsInvalid)
        {
            return 0;
        }

        int physical = Marshal.GetLastPInvokeError();
        disk.Dispose();
        disk = CreateFile($@"\\.\{letter}:", GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        return disk.IsInvalid ? physical : 0;
    }

    public static unsafe int TryScsiCommand(SafeFileHandle disk, ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
    {
        reply = new ScsiReply(0, 0, []);
        if (cdb.Length is 0 or > MaxCdbBytes || data.Length > CommandDataBytes || (direction == ScsiDirection.None && !data.IsEmpty))
        {
            return ErrorInvalidParameter;
        }

        var request = new CommandPassThrough
        {
            Length = PassThroughHeaderBytes,
            CdbLength = (byte)cdb.Length,
            SenseInfoLength = CommandSenseBytes,
            DataIn = (byte)direction,
            DataTransferLength = (uint)data.Length,
            TimeOutValue = CommandTimeoutSeconds,
            DataBufferOffset = CommandDataOffset,
            SenseInfoOffset = PassThroughSenseOffset,
        };
        cdb.CopyTo(new Span<byte>(request.Cdb, MaxCdbBytes));
        if (direction == ScsiDirection.Out)
        {
            data.CopyTo(new Span<byte>(request.Data, CommandDataBytes));
        }

        if (!DeviceIoControl(disk, IoctlScsiPassThrough, &request, (uint)sizeof(CommandPassThrough), &request, (uint)sizeof(CommandPassThrough), out _, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            return error is ErrorInvalidFunction or ErrorNotSupported ? TryScsiDirect(disk, cdb, direction, data, out reply) : error;
        }

        int transferred = (int)Math.Min(request.DataTransferLength, (uint)data.Length);
        if (direction == ScsiDirection.In)
        {
            new ReadOnlySpan<byte>(request.Data, transferred).CopyTo(data);
        }

        reply = new ScsiReply(request.ScsiStatus, transferred, new ReadOnlySpan<byte>(request.Sense, CommandSenseBytes).ToArray());
        return 0;
    }

    public static int TryGetUsbParent(uint diskNumber, out string hardwareId)
    {
        hardwareId = string.Empty;
        int error = TryFindDiskInstance(diskNumber, out string instance);
        return error != 0 ? error : TryFindUsbAncestor(instance, out hardwareId);
    }

    private static unsafe int TryScsiDirect(SafeFileHandle disk, ReadOnlySpan<byte> cdb, ScsiDirection direction, Span<byte> data, out ScsiReply reply)
    {
        reply = new ScsiReply(0, 0, []);
        using var buffer = new AlignedBuffer(CommandDataBytes);
        Span<byte> transfer = buffer.Span;
        transfer.Clear();
        if (direction == ScsiDirection.Out)
        {
            data.CopyTo(transfer);
        }

        fixed (byte* pointer = transfer)
        {
            var request = new DirectPassThrough
            {
                Length = DirectHeaderBytes,
                CdbLength = (byte)cdb.Length,
                SenseInfoLength = CommandSenseBytes,
                DataIn = (byte)direction,
                DataTransferLength = (uint)data.Length,
                TimeOutValue = CommandTimeoutSeconds,
                DataBuffer = data.IsEmpty ? 0 : (nint)pointer,
                SenseInfoOffset = DirectSenseOffset,
            };
            cdb.CopyTo(new Span<byte>(request.Cdb, MaxCdbBytes));
            if (!DeviceIoControl(disk, IoctlScsiPassThroughDirect, &request, (uint)sizeof(DirectPassThrough), &request, (uint)sizeof(DirectPassThrough), out _, 0))
            {
                return Marshal.GetLastPInvokeError();
            }

            int transferred = (int)Math.Min(request.DataTransferLength, (uint)data.Length);
            if (direction == ScsiDirection.In)
            {
                transfer[..transferred].CopyTo(data);
            }

            reply = new ScsiReply(request.ScsiStatus, transferred, new ReadOnlySpan<byte>(request.Sense, CommandSenseBytes).ToArray());
            return 0;
        }
    }

    private static unsafe int TryFindDiskInstance(uint diskNumber, out string instance)
    {
        instance = string.Empty;
        Guid diskInterface = DiskInterface;
        if (CM_Get_Device_Interface_List_Size(out uint length, &diskInterface, null, InterfaceListPresent) != ConfigSuccess || length <= 1)
        {
            return ErrorFileNotFound;
        }

        char[] list = new char[length];
        fixed (char* chars = list)
        {
            if (CM_Get_Device_Interface_List(&diskInterface, null, chars, length, InterfaceListPresent) != ConfigSuccess)
            {
                return ErrorFileNotFound;
            }
        }

        foreach (string path in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            using SafeFileHandle device = CreateFile(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
            if (device.IsInvalid || TryGetDeviceNumber(device, out uint number) != 0 || number != diskNumber)
            {
                continue;
            }

            byte* value = stackalloc byte[InstanceIdBytes];
            uint size = InstanceIdBytes;
            var key = new DevicePropertyKey { Category = InstanceIdKey, Id = InstanceIdProperty };
            fixed (char* name = path)
            {
                if (CM_Get_Device_Interface_Property(name, &key, out _, value, ref size, 0) != ConfigSuccess)
                {
                    return ErrorFileNotFound;
                }
            }

            instance = new string((char*)value, 0, (int)(size / sizeof(char))).TrimEnd('\0');
            return instance.Length == 0 ? ErrorFileNotFound : 0;
        }

        return ErrorFileNotFound;
    }

    private static unsafe int TryFindUsbAncestor(string instance, out string hardwareId)
    {
        hardwareId = string.Empty;
        uint node;
        fixed (char* id = instance)
        {
            if (CM_Locate_DevNode(out node, id, 0) != ConfigSuccess)
            {
                return ErrorFileNotFound;
            }
        }

        char* text = stackalloc char[DeviceIdChars];
        for (int depth = 0; depth < MaxParents; depth++)
        {
            if (CM_Get_Parent(out uint parent, node, 0) != ConfigSuccess || CM_Get_Device_ID(parent, text, DeviceIdChars, 0) != ConfigSuccess)
            {
                return ErrorFileNotFound;
            }

            string found = new(text);
            if (found.StartsWith(UsbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                hardwareId = found;
                return 0;
            }

            node = parent;
        }

        return ErrorFileNotFound;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static unsafe partial uint CM_Get_Device_Interface_List_Size(out uint length, Guid* interfaceClass, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static unsafe partial uint CM_Get_Device_Interface_List(Guid* interfaceClass, char* deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW")]
    private static unsafe partial uint CM_Get_Device_Interface_Property(char* deviceInterface, DevicePropertyKey* key, out uint type, byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW")]
    private static unsafe partial uint CM_Locate_DevNode(out uint node, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial uint CM_Get_Device_ID(uint node, char* buffer, uint length, uint flags);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct DevicePropertyKey
    {
        [FieldOffset(0)]
        public Guid Category;

        [FieldOffset(16)]
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = CommandDataOffset + CommandDataBytes)]
    private unsafe struct CommandPassThrough
    {
        [FieldOffset(0)]
        public ushort Length;

        [FieldOffset(2)]
        public byte ScsiStatus;

        [FieldOffset(6)]
        public byte CdbLength;

        [FieldOffset(7)]
        public byte SenseInfoLength;

        [FieldOffset(8)]
        public byte DataIn;

        [FieldOffset(12)]
        public uint DataTransferLength;

        [FieldOffset(16)]
        public uint TimeOutValue;

        [FieldOffset(24)]
        public ulong DataBufferOffset;

        [FieldOffset(32)]
        public uint SenseInfoOffset;

        [FieldOffset(36)]
        public fixed byte Cdb[MaxCdbBytes];

        [FieldOffset(56)]
        public fixed byte Sense[CommandSenseBytes];

        [FieldOffset(CommandDataOffset)]
        public fixed byte Data[CommandDataBytes];
    }

    [StructLayout(LayoutKind.Explicit, Size = DirectBufferBytes)]
    private unsafe struct DirectPassThrough
    {
        [FieldOffset(0)]
        public ushort Length;

        [FieldOffset(2)]
        public byte ScsiStatus;

        [FieldOffset(6)]
        public byte CdbLength;

        [FieldOffset(7)]
        public byte SenseInfoLength;

        [FieldOffset(8)]
        public byte DataIn;

        [FieldOffset(12)]
        public uint DataTransferLength;

        [FieldOffset(16)]
        public uint TimeOutValue;

        [FieldOffset(24)]
        public nint DataBuffer;

        [FieldOffset(32)]
        public uint SenseInfoOffset;

        [FieldOffset(36)]
        public fixed byte Cdb[MaxCdbBytes];

        [FieldOffset(DirectSenseOffset)]
        public fixed byte Sense[CommandSenseBytes];
    }
}
