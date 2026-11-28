using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Niskala.Interop.Volumes;

public readonly record struct VolumeExtent(long Vcn, long NextVcn, long Lcn);

public readonly record struct VolumeFacts(string Label, string FileSystem, long BytesPerCluster, long TotalClusters, long FreeClusters, long TotalBytes, long FreeBytes, uint SerialNumber = 0);

public readonly record struct MftRecord(long Id, long Parent, FileAttributes Attributes, string Name)
{
    public const long RecordMask = 0x0000_FFFF_FFFF_FFFF;

    public long Number => Id & RecordMask;

    public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
}

public readonly record struct RecordFacts(long Size, DateTime LastWriteUtc);

public readonly record struct DiskCounters(long BytesRead, long BytesWritten, int QueueDepth, long QueryTime);

public readonly record struct FoundEntry(string Name, FileAttributes Attributes, long Size, DateTime LastWriteUtc);

public readonly record struct NtfsLayout(
    long MftStartLcn,
    long MftClusters,
    long MftZoneStart,
    long MftZoneEnd,
    long FileRecords,
    long BytesPerCluster = 0,
    int BytesPerSector = 0,
    int BytesPerFileRecord = 0,
    long MftValidBytes = 0);

public readonly record struct StorageDeviceFacts(int BusType, string Vendor, string Product);

public static partial class VolumeNative
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidData = 13;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorHandleEof = 38;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorDiskFull = 112;
    public const int ErrorMoreData = 234;

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlScsiPassThrough = 0x0004D004;
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetVolumeBitmap = 0x0009006F;
    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const uint FsctlMoveFile = 0x00090074;
    private const uint FsctlFileLevelTrim = 0x00098208;
    private const uint FsctlEnumUsnData = 0x000900B3;

    private const int UsnBufferBytes = 1 << 20;
    private const ushort UsnRecordV2 = 2;
    private const ushort UsnRecordV3 = 3;
    private const int UsnRecordV2Header = 60;
    private const int UsnRecordV3Header = 76;
    private const int MftEnumDataV0Bytes = 24;
    private const int FileIdType = 0;
    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;

    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceTrimProperty = 8;
    private const int PropertyStandardQuery = 0;
    private const int StorageDeviceProperty = 0;
    private const int DeviceDescriptorBytes = 1024;

    private const byte ScsiInquiry = 0x12;
    private const byte InquiryVitalProductData = 0x01;
    private const byte BlockDeviceCharacteristicsPage = 0xB1;
    private const byte ScsiDataIn = 1;
    private const byte InquiryCdbLength = 6;
    private const int PassThroughHeaderBytes = 56;
    private const int PassThroughSenseOffset = 56;
    private const int PassThroughSenseBytes = 32;
    private const int PassThroughDataOffset = 96;
    private const int PassThroughDataBytes = 64;
    private const uint PassThroughTimeoutSeconds = 2;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x7;
    private const uint ShareReadWrite = 0x3;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagDeleteOnClose = 0x04000000;
    private const uint AttributeHidden = 0x2;
    private const uint AttributeTemporary = 0x100;

    private const uint DriveFixed = 3;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const uint FindFirstExLargeFetch = 2;
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;
    private const int FileEndOfFileInfo = 6;
    private const int FileIoPriorityHintInfo = 12;
    private const int IoPriorityVeryLow = 0;
    private const int RetrievalBatch = 512;
    private const int BitmapChunkBytes = 1 << 20;
    private const int VolumeNameLength = 261;
    private const string ExtendedPrefix = @"\\?\";
    private static readonly nint InvalidHandle = -1;

    public static IReadOnlyList<char> FixedDrives()
    {
        uint mask = GetLogicalDrives();
        var drives = new List<char>();
        for (int bit = 0; bit < 26; bit++)
        {
            char letter = (char)('A' + bit);
            if ((mask & (1u << bit)) != 0 && GetDriveType($"{letter}:\\") == DriveFixed)
            {
                drives.Add(letter);
            }
        }

        return drives;
    }

    public static unsafe int TryGetFacts(char letter, out VolumeFacts facts)
    {
        facts = default;
        string root = $"{letter}:\\";
        char* label = stackalloc char[VolumeNameLength];
        char* fileSystem = stackalloc char[VolumeNameLength];
        if (!GetVolumeInformation(root, label, VolumeNameLength, out uint serial, out _, out _, fileSystem, VolumeNameLength))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (!GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (!GetDiskFreeSpaceEx(root, out _, out ulong totalBytes, out ulong freeBytes))
        {
            return Marshal.GetLastPInvokeError();
        }

        long cluster = (long)sectorsPerCluster * bytesPerSector;
        facts = new VolumeFacts(
            new string(label),
            new string(fileSystem),
            cluster,
            cluster > 0 ? Math.Max(totalClusters, (long)(totalBytes / (ulong)cluster)) : totalClusters,
            cluster > 0 ? Math.Max(freeClusters, (long)(freeBytes / (ulong)cluster)) : freeClusters,
            (long)totalBytes,
            (long)freeBytes,
            serial);
        return 0;
    }

    public static int TryOpenVolume(char letter, bool readData, out SafeFileHandle volume)
    {
        volume = CreateFile($@"\\.\{letter}:", readData ? GenericRead : 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        return volume.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    public static int TryOpenEntry(string path, out SafeFileHandle entry)
    {
        entry = CreateFile(Extended(path), FileReadAttributes | Synchronize, ShareAll, 0, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, 0);
        return entry.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    public static int TryCreateScratch(string path, out SafeFileHandle file)
    {
        file = CreateFile(Extended(path), GenericRead | GenericWrite | Delete, 0, 0, CreateNew, AttributeHidden | AttributeTemporary | FlagDeleteOnClose, 0);
        return file.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    public static int TryQuerySeekPenalty(SafeFileHandle volume, out bool incursSeekPenalty) =>
        TryQueryFlag(volume, StorageDeviceSeekPenaltyProperty, out incursSeekPenalty);

    public static int TryQueryTrim(SafeFileHandle volume, out bool trimEnabled) =>
        TryQueryFlag(volume, StorageDeviceTrimProperty, out trimEnabled);

    public static unsafe int TryQueryDevice(SafeFileHandle volume, out StorageDeviceFacts facts)
    {
        facts = new StorageDeviceFacts(0, string.Empty, string.Empty);
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceProperty, QueryType = PropertyStandardQuery };
        byte* buffer = stackalloc byte[DeviceDescriptorBytes];
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), buffer, DeviceDescriptorBytes, out uint returned, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (returned < sizeof(DeviceDescriptor))
        {
            return ErrorInvalidData;
        }

        var descriptor = (DeviceDescriptor*)buffer;
        var bytes = new ReadOnlySpan<byte>(buffer, (int)Math.Min(returned, (uint)DeviceDescriptorBytes));
        facts = new StorageDeviceFacts(descriptor->BusType, Ascii(bytes, descriptor->VendorIdOffset), Ascii(bytes, descriptor->ProductIdOffset));
        return 0;
    }

    public static unsafe int TryQueryRotationRate(char letter, out int rotationRate)
    {
        rotationRate = 0;
        if (sizeof(nint) != sizeof(long))
        {
            return ErrorNotSupported;
        }

        using SafeFileHandle device = CreateFile($@"\\.\{letter}:", GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (device.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        var request = new InquiryPassThrough
        {
            Length = PassThroughHeaderBytes,
            CdbLength = InquiryCdbLength,
            SenseInfoLength = PassThroughSenseBytes,
            DataIn = ScsiDataIn,
            DataTransferLength = PassThroughDataBytes,
            TimeOutValue = PassThroughTimeoutSeconds,
            DataBufferOffset = PassThroughDataOffset,
            SenseInfoOffset = PassThroughSenseOffset,
        };
        request.Cdb[0] = ScsiInquiry;
        request.Cdb[1] = InquiryVitalProductData;
        request.Cdb[2] = BlockDeviceCharacteristicsPage;
        request.Cdb[4] = PassThroughDataBytes;
        if (!DeviceIoControl(device, IoctlScsiPassThrough, &request, (uint)sizeof(InquiryPassThrough), &request, (uint)sizeof(InquiryPassThrough), out _, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        if (request.ScsiStatus != 0 || request.Data[1] != BlockDeviceCharacteristicsPage)
        {
            return ErrorNotSupported;
        }

        rotationRate = (request.Data[4] << 8) | request.Data[5];
        return 0;
    }

    public static unsafe int TryReadBitmap(SafeFileHandle volume, out ulong[] words, out long clusters)
    {
        words = [];
        clusters = 0;
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)(sizeof(VolumeBitmapHeader) + BitmapChunkBytes));
        try
        {
            long start = 0;
            while (true)
            {
                bool done = DeviceIoControl(volume, FsctlGetVolumeBitmap, &start, sizeof(long), buffer, (uint)(sizeof(VolumeBitmapHeader) + BitmapChunkBytes), out uint returned, 0);
                int error = done ? 0 : Marshal.GetLastPInvokeError();
                if (!done && error != ErrorMoreData)
                {
                    return error;
                }

                var header = *(VolumeBitmapHeader*)buffer;
                if (words.Length == 0)
                {
                    clusters = header.StartingLcn + header.BitmapSize;
                    words = new ulong[(clusters + 63) / 64];
                }

                Span<byte> bits = MemoryMarshal.AsBytes(words.AsSpan());
                long bytes = Math.Min(returned - sizeof(VolumeBitmapHeader), (header.BitmapSize + 7) / 8);
                long offset = header.StartingLcn / 8;
                bytes = Math.Min(bytes, bits.Length - offset);
                new ReadOnlySpan<byte>(buffer + sizeof(VolumeBitmapHeader), (int)bytes).CopyTo(bits[(int)offset..]);
                if (done || bytes <= 0)
                {
                    return 0;
                }

                start = header.StartingLcn + (bytes * 8);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public static unsafe int TryReadBitmapRange(SafeFileHandle volume, long start, long clusters, out long firstLcn, out byte[] bits)
    {
        firstLcn = 0;
        bits = [];
        if (start < 0 || clusters <= 0)
        {
            return ErrorInvalidParameter;
        }

        long from = start / 8 * 8;
        int bytes = (int)Math.Min(BitmapChunkBytes, (start + clusters - from + 7) / 8);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)(sizeof(VolumeBitmapHeader) + bytes));
        try
        {
            bool done = DeviceIoControl(volume, FsctlGetVolumeBitmap, &from, sizeof(long), buffer, (uint)(sizeof(VolumeBitmapHeader) + bytes), out uint returned, 0);
            int error = done ? 0 : Marshal.GetLastPInvokeError();
            if (!done && error != ErrorMoreData)
            {
                return error;
            }

            var header = *(VolumeBitmapHeader*)buffer;
            long available = Math.Min(returned - sizeof(VolumeBitmapHeader), (header.BitmapSize + 7) / 8);
            firstLcn = header.StartingLcn;
            bits = new ReadOnlySpan<byte>(buffer + sizeof(VolumeBitmapHeader), (int)Math.Clamp(available, 0, bytes)).ToArray();
            return 0;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public static unsafe int TryEnumerateMft(SafeFileHandle volume, List<MftRecord> records, Action<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        var input = new MftEnumData { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue, MinMajorVersion = UsnRecordV2, MaxMajorVersion = UsnRecordV3 };
        uint inputBytes = (uint)sizeof(MftEnumData);
        byte* buffer = (byte*)NativeMemory.Alloc(UsnBufferBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DeviceIoControl(volume, FsctlEnumUsnData, &input, inputBytes, buffer, UsnBufferBytes, out uint returned, 0))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorHandleEof)
                    {
                        return 0;
                    }

                    if (error == ErrorInvalidParameter && inputBytes != MftEnumDataV0Bytes && records.Count == 0)
                    {
                        inputBytes = MftEnumDataV0Bytes;
                        continue;
                    }

                    return error;
                }

                if (returned <= sizeof(long))
                {
                    return 0;
                }

                long next = *(long*)buffer;
                if (!ParseUsnRecords(new ReadOnlySpan<byte>(buffer + sizeof(long), (int)returned - sizeof(long)), records))
                {
                    return ErrorNotSupported;
                }

                progress?.Invoke(records.Count);
                if (next == input.StartFileReferenceNumber)
                {
                    return 0;
                }

                input.StartFileReferenceNumber = next;
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public static bool ParseUsnRecords(ReadOnlySpan<byte> buffer, List<MftRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        int offset = 0;
        while (buffer.Length - offset >= sizeof(uint) * 2)
        {
            ReadOnlySpan<byte> rest = buffer[offset..];
            int length = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(rest));
            if (length < sizeof(uint) * 2 || length > rest.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> record = rest[..length];
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            long id, parent;
            uint attributes;
            int nameBytes, nameOffset;
            if (major == UsnRecordV2 && length >= UsnRecordV2Header)
            {
                id = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
                parent = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
                attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            }
            else if (major == UsnRecordV3 && length >= UsnRecordV3Header)
            {
                if (BinaryPrimitives.ReadInt64LittleEndian(record[16..]) != 0 || BinaryPrimitives.ReadInt64LittleEndian(record[32..]) != 0)
                {
                    return false;
                }

                id = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
                parent = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
                attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
                nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[72..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[74..]);
            }
            else
            {
                return false;
            }

            if (nameBytes % sizeof(char) != 0 || nameOffset + nameBytes > length)
            {
                return false;
            }

            records.Add(new MftRecord(id, parent, (FileAttributes)attributes, System.Text.Encoding.Unicode.GetString(record.Slice(nameOffset, nameBytes))));
            offset += length;
        }

        return true;
    }

    public static unsafe int TryOpenById(SafeFileHandle hint, long id, out SafeFileHandle entry)
    {
        var descriptor = new FileIdDescriptor { Size = (uint)sizeof(FileIdDescriptor), Type = FileIdType, FileId = id };
        entry = OpenFileById(hint, &descriptor, FileReadAttributes | Synchronize, ShareAll, 0, FlagBackupSemantics | FlagOpenReparsePoint);
        return entry.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    public static unsafe int TryReadFacts(SafeFileHandle entry, out RecordFacts facts)
    {
        facts = default;
        FileBasicInformation basic;
        FileStandardInformation standard;
        if (!GetFileInformationByHandleEx(entry, FileBasicInfoClass, &basic, (uint)sizeof(FileBasicInformation))
            || !GetFileInformationByHandleEx(entry, FileStandardInfoClass, &standard, (uint)sizeof(FileStandardInformation)))
        {
            return Marshal.GetLastPInvokeError();
        }

        facts = new RecordFacts(standard.EndOfFile, DateTime.FromFileTimeUtc(Math.Max(0, basic.LastWriteTime)));
        return 0;
    }

    public static unsafe int TryGetExtents(SafeFileHandle entry, List<VolumeExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        int size = sizeof(RetrievalHeader) + (RetrievalBatch * sizeof(RetrievalExtent));
        byte* buffer = stackalloc byte[size];
        long vcn = 0;
        while (true)
        {
            long start = vcn;
            bool done = DeviceIoControl(entry, FsctlGetRetrievalPointers, &start, sizeof(long), buffer, (uint)size, out _, 0);
            int error = done ? 0 : Marshal.GetLastPInvokeError();
            if (!done && error == ErrorHandleEof)
            {
                return 0;
            }

            if (!done && error != ErrorMoreData)
            {
                return error;
            }

            var header = *(RetrievalHeader*)buffer;
            var batch = (RetrievalExtent*)(buffer + sizeof(RetrievalHeader));
            long previous = header.StartingVcn;
            for (int i = 0; i < header.ExtentCount && i < RetrievalBatch; i++)
            {
                extents.Add(new VolumeExtent(previous, batch[i].NextVcn, batch[i].Lcn));
                previous = batch[i].NextVcn;
            }

            if (done || header.ExtentCount == 0)
            {
                return 0;
            }

            vcn = previous;
        }
    }

    public static unsafe int TryMove(SafeFileHandle volume, SafeFileHandle file, long vcn, long lcn, uint clusters)
    {
        bool added = false;
        file.DangerousAddRef(ref added);
        try
        {
            var data = new MoveFileData { FileHandle = file.DangerousGetHandle(), StartingVcn = vcn, StartingLcn = lcn, ClusterCount = clusters };
            return DeviceIoControl(volume, FsctlMoveFile, &data, (uint)sizeof(MoveFileData), null, 0, out _, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (added)
            {
                file.DangerousRelease();
            }
        }
    }

    public static unsafe int TryGetNtfsLayout(SafeFileHandle volume, out NtfsLayout layout)
    {
        layout = default;
        NtfsVolumeData data;
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, null, 0, &data, (uint)sizeof(NtfsVolumeData), out _, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        long clusters = data.BytesPerCluster == 0 ? 0 : (data.MftValidDataLength + data.BytesPerCluster - 1) / data.BytesPerCluster;
        long records = data.BytesPerFileRecordSegment == 0 ? 0 : data.MftValidDataLength / data.BytesPerFileRecordSegment;
        layout = new NtfsLayout(
            data.MftStartLcn,
            clusters,
            data.MftZoneStart,
            data.MftZoneEnd,
            records,
            data.BytesPerCluster,
            (int)Math.Min(int.MaxValue, data.BytesPerSector),
            (int)Math.Min(int.MaxValue, data.BytesPerFileRecordSegment),
            data.MftValidDataLength);
        return 0;
    }

    public static unsafe int TryReadVolume(SafeFileHandle volume, long offset, Span<byte> buffer, out int read)
    {
        read = 0;
        var overlapped = new ReadOverlapped { Offset = (uint)offset, OffsetHigh = (uint)(offset >> 32) };
        fixed (byte* data = buffer)
        {
            if (!ReadFile(volume, data, (uint)buffer.Length, out uint done, &overlapped))
            {
                return Marshal.GetLastPInvokeError();
            }

            read = (int)done;
            return 0;
        }
    }

    public static unsafe int TryReadCounters(SafeFileHandle volume, out DiskCounters counters)
    {
        counters = default;
        DiskPerformance performance;
        if (!DeviceIoControl(volume, IoctlDiskPerformance, null, 0, &performance, (uint)sizeof(DiskPerformance), out _, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        counters = new DiskCounters(performance.BytesRead, performance.BytesWritten, (int)performance.QueueDepth, performance.QueryTime);
        return 0;
    }

    public static unsafe int TrySetLength(SafeFileHandle file, long length) =>
        SetFileInformationByHandle(file, FileEndOfFileInfo, &length, sizeof(long)) ? 0 : Marshal.GetLastPInvokeError();

    public static unsafe int TryTrim(SafeFileHandle file, long offset, long length)
    {
        var trim = new FileLevelTrim { Key = 0, NumRanges = 1, Offset = (ulong)offset, Length = (ulong)length };
        uint processed;
        return DeviceIoControl(file, FsctlFileLevelTrim, &trim, (uint)sizeof(FileLevelTrim), &processed, sizeof(uint), out _, 0) ? 0 : Marshal.GetLastPInvokeError();
    }

    public static unsafe int TrySetLowIoPriority(SafeFileHandle handle)
    {
        int hint = IoPriorityVeryLow;
        return SetFileInformationByHandle(handle, FileIoPriorityHintInfo, &hint, sizeof(int)) ? 0 : Marshal.GetLastPInvokeError();
    }

    public static bool EnterBackgroundMode() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);

    public static bool LeaveBackgroundMode() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);

    public static unsafe int TryList(string directory, List<FoundEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        FindData data;
        nint find = FindFirstFileEx(Path.Join(Extended(directory), "*"), FindExInfoBasic, &data, FindExSearchNameMatch, null, FindFirstExLargeFetch);
        if (find == InvalidHandle)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorFileNotFound ? 0 : error;
        }

        try
        {
            do
            {
                string name = new(data.FileName);
                if (name is "." or "..")
                {
                    continue;
                }

                long size = ((long)data.SizeHigh << 32) | data.SizeLow;
                long written = ((long)data.LastWriteHigh << 32) | data.LastWriteLow;
                entries.Add(new FoundEntry(name, (FileAttributes)data.Attributes, size, DateTime.FromFileTimeUtc(Math.Max(0, written))));
            }
            while (FindNextFile(find, &data));

            int last = Marshal.GetLastPInvokeError();
            return last == ErrorNoMoreFiles ? 0 : last;
        }
        finally
        {
            FindClose(find);
        }
    }

    private static string Ascii(ReadOnlySpan<byte> descriptor, uint offset)
    {
        if (offset == 0 || offset >= descriptor.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> text = descriptor[(int)offset..];
        int end = text.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
    }

    private static string Extended(string path) =>
        path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path : ExtendedPrefix + Path.GetFullPath(path);

    private static unsafe int TryQueryFlag(SafeFileHandle volume, int property, out bool value)
    {
        value = false;
        var query = new StoragePropertyQuery { PropertyId = property, QueryType = PropertyStandardQuery };
        BooleanDescriptor descriptor;
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), &descriptor, (uint)sizeof(BooleanDescriptor), out _, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        value = descriptor.Value != 0;
        return 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(string root, char* label, uint labelSize, out uint serial, out uint maxComponent, out uint flags, char* fileSystem, uint fileSystemSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string root, out ulong freeToCaller, out ulong totalBytes, out ulong freeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string root);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetLogicalDrives();

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint FindFirstFileEx(string pattern, int infoLevel, FindData* data, int searchOp, void* filter, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextFile(nint find, FindData* data);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(SafeFileHandle file, byte* buffer, uint bytes, out uint read, ReadOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial SafeFileHandle OpenFileById(SafeFileHandle hint, FileIdDescriptor* id, uint access, uint share, nint security, uint flags);

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct MftEnumData
    {
        [FieldOffset(0)]
        public long StartFileReferenceNumber;

        [FieldOffset(8)]
        public long LowUsn;

        [FieldOffset(16)]
        public long HighUsn;

        [FieldOffset(24)]
        public ushort MinMajorVersion;

        [FieldOffset(26)]
        public ushort MaxMajorVersion;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public int Type;

        [FieldOffset(8)]
        public long FileId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct FileBasicInformation
    {
        [FieldOffset(16)]
        public long LastWriteTime;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileStandardInformation
    {
        [FieldOffset(8)]
        public long EndOfFile;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct StoragePropertyQuery
    {
        [FieldOffset(0)]
        public int PropertyId;

        [FieldOffset(4)]
        public int QueryType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct BooleanDescriptor
    {
        [FieldOffset(8)]
        public byte Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct DeviceDescriptor
    {
        [FieldOffset(12)]
        public uint VendorIdOffset;

        [FieldOffset(16)]
        public uint ProductIdOffset;

        [FieldOffset(28)]
        public int BusType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 160)]
    private unsafe struct InquiryPassThrough
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
        public fixed byte Cdb[16];

        [FieldOffset(56)]
        public fixed byte Sense[32];

        [FieldOffset(96)]
        public fixed byte Data[64];
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct VolumeBitmapHeader
    {
        [FieldOffset(0)]
        public long StartingLcn;

        [FieldOffset(8)]
        public long BitmapSize;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct RetrievalHeader
    {
        [FieldOffset(0)]
        public uint ExtentCount;

        [FieldOffset(8)]
        public long StartingVcn;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct RetrievalExtent
    {
        [FieldOffset(0)]
        public long NextVcn;

        [FieldOffset(8)]
        public long Lcn;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct MoveFileData
    {
        [FieldOffset(0)]
        public nint FileHandle;

        [FieldOffset(8)]
        public long StartingVcn;

        [FieldOffset(16)]
        public long StartingLcn;

        [FieldOffset(24)]
        public uint ClusterCount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReadOverlapped
    {
        [FieldOffset(16)]
        public uint Offset;

        [FieldOffset(20)]
        public uint OffsetHigh;
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct NtfsVolumeData
    {
        [FieldOffset(40)]
        public uint BytesPerSector;

        [FieldOffset(44)]
        public uint BytesPerCluster;

        [FieldOffset(48)]
        public uint BytesPerFileRecordSegment;

        [FieldOffset(56)]
        public long MftValidDataLength;

        [FieldOffset(64)]
        public long MftStartLcn;

        [FieldOffset(80)]
        public long MftZoneStart;

        [FieldOffset(88)]
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct DiskPerformance
    {
        [FieldOffset(0)]
        public long BytesRead;

        [FieldOffset(8)]
        public long BytesWritten;

        [FieldOffset(48)]
        public uint QueueDepth;

        [FieldOffset(56)]
        public long QueryTime;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileLevelTrim
    {
        [FieldOffset(0)]
        public uint Key;

        [FieldOffset(4)]
        public uint NumRanges;

        [FieldOffset(8)]
        public ulong Offset;

        [FieldOffset(16)]
        public ulong Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 592)]
    private unsafe struct FindData
    {
        [FieldOffset(0)]
        public uint Attributes;

        [FieldOffset(20)]
        public uint LastWriteLow;

        [FieldOffset(24)]
        public uint LastWriteHigh;

        [FieldOffset(28)]
        public uint SizeHigh;

        [FieldOffset(32)]
        public uint SizeLow;

        [FieldOffset(44)]
        public fixed char FileName[260];
    }
}
