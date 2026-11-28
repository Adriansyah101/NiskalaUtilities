using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public delegate int VolumeRead(long offset, Span<byte> buffer, out int read);

public readonly record struct MftGeometry(long BytesPerCluster, int BytesPerSector, int BytesPerRecord, long MftStartLcn, long MftValidBytes)
{
    public const int SmallestRecord = 256;
    public const int LargestRecord = 1 << 16;
    public const int SmallestSector = 256;
    public const long LargestCluster = 1L << 21;

    public bool IsUsable =>
        BytesPerSector >= SmallestSector && BitOperations.IsPow2(BytesPerSector)
        && BytesPerRecord >= SmallestRecord && BytesPerRecord <= LargestRecord && BitOperations.IsPow2(BytesPerRecord)
        && BytesPerCluster >= BytesPerSector && BytesPerCluster <= LargestCluster && BitOperations.IsPow2(BytesPerCluster)
        && MftStartLcn > 0 && MftValidBytes >= BytesPerRecord;
}

public readonly record struct FileRecord(MftRecord Entry, long Size, DateTime LastWriteUtc, Extent[]? Extents)
{
    public bool NeedsHandle => Extents is null;

    public bool IsResident => Extents is { Length: 0 };
}

public readonly record struct StreamPiece(StreamKind Kind, long LowestVcn, long HighestVcn, long AllocatedBytes, long DataBytes, Extent[]? Runs);

public enum StreamKind : byte
{
    Data,
    Index,
}

public static class NtfsMftRecordReader
{
    public const int ChunkBytes = 4 << 20;
    public const uint FileSignature = 0x454C4946;
    public const uint EndMarker = 0xFFFF_FFFF;
    public const uint StandardInformation = 0x10;
    public const uint AttributeList = 0x20;
    public const uint FileName = 0x30;
    public const uint Data = 0x80;
    public const uint IndexAllocation = 0xA0;
    public const ushort InUseFlag = 0x01;
    public const ushort DirectoryFlag = 0x02;
    public const byte DosNamespace = 2;
    public const string DirectoryIndex = "$I30";

    private const int UsaOffsetField = 0x04;
    private const int UsaCountField = 0x06;
    private const int SequenceField = 0x10;
    private const int FirstAttributeField = 0x14;
    private const int FlagsField = 0x16;
    private const int BytesInUseField = 0x18;
    private const int BaseRecordField = 0x20;
    private const int HeaderBytes = 0x30;
    private const int AttributeHeaderBytes = 0x10;
    private const int LengthField = 0x04;
    private const int NonResidentField = 0x08;
    private const int NameLengthField = 0x09;
    private const int NameOffsetField = 0x0A;
    private const int ValueLengthField = 0x10;
    private const int ValueOffsetField = 0x14;
    private const int ResidentHeaderBytes = 0x18;
    private const int LowestVcnField = 0x10;
    private const int HighestVcnField = 0x18;
    private const int PairsOffsetField = 0x20;
    private const int AllocatedField = 0x28;
    private const int DataSizeField = 0x30;
    private const int NonResidentHeaderBytes = 0x40;
    private const int LastWriteField = 0x08;
    private const int FileAttributesField = 0x20;
    private const int StandardInformationBytes = 0x24;
    private const int ParentField = 0x00;
    private const int NameCharsField = 0x40;
    private const int NamespaceField = 0x41;
    private const int NameField = 0x42;
    private const long LargestFileTime = 0x24C8_5A5E_D1C0_3FFF;

    public static int Read(MftGeometry geometry, VolumeRead read, Action<FileRecord> record, Action<int>? progress, CancellationToken cancellationToken, out int damaged)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(record);
        damaged = 0;
        if (!geometry.IsUsable)
        {
            return VolumeNative.ErrorNotSupported;
        }

        int recordBytes = geometry.BytesPerRecord;
        long cluster = geometry.BytesPerCluster;
        int chunk = (int)(((Math.Max(ChunkBytes, cluster) + cluster - 1) / cluster) * cluster);
        using var buffer = new AlignedBuffer(chunk + recordBytes, Math.Max(AlignedBuffer.DefaultAlignment, geometry.BytesPerSector));
        Span<byte> span = buffer.Span;
        var parsed = new ParsedRecord();
        var scratch = new List<Extent>();
        int first = (int)RoundUp(recordBytes, geometry.BytesPerSector);
        int error = read(geometry.MftStartLcn * cluster, span[..first], out int got);
        if (error != 0)
        {
            return error;
        }

        if (got < recordBytes || !IsInUse(span[..recordBytes]) || !TryFixup(span[..recordBytes]) || !TryParse(span[..recordBytes], parsed, scratch)
            || Join(parsed.Pieces, StreamKind.Data, cluster, out long tableBytes) is not { } tableRuns
            || tableRuns.Any(r => r.IsVirtual))
        {
            return VolumeNative.ErrorNotSupported;
        }

        long slots = Math.Min(geometry.MftValidBytes, tableBytes) / recordBytes;
        var merge = new Dictionary<long, Pending>();
        long number = 0;
        int carry = 0;
        foreach (Extent run in tableRuns)
        {
            long runBytes = run.Clusters * cluster;
            for (long done = 0; done < runBytes && number < slots;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long wanted = RoundUp(((slots - number) * recordBytes) - carry, cluster);
                int length = (int)Math.Min(Math.Min(runBytes - done, (span.Length - carry) / cluster * cluster), wanted);
                error = read((run.Lcn * cluster) + done, span.Slice(carry, length), out got);
                if (error != 0)
                {
                    return error;
                }

                if (got != length)
                {
                    return VolumeNative.ErrorInvalidData;
                }

                done += length;
                int available = carry + length;
                int offset = 0;
                for (; available - offset >= recordBytes && number < slots; offset += recordBytes, number++)
                {
                    if (!Visit(span.Slice(offset, recordBytes), number, cluster, parsed, scratch, record, merge))
                    {
                        damaged++;
                    }
                }

                carry = available - offset;
                if (carry > 0)
                {
                    span.Slice(offset, carry).CopyTo(span);
                }

                progress?.Invoke((int)Math.Min(int.MaxValue, number));
            }
        }

        if (number < slots)
        {
            return VolumeNative.ErrorInvalidData;
        }

        damaged += Finish(record, merge, cluster);
        return 0;
    }

    public static bool TryFixup(Span<byte> record)
    {
        if (record.Length < HeaderBytes)
        {
            return false;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[UsaOffsetField..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[UsaCountField..]);
        if (usaCount < 2 || usaOffset % sizeof(ushort) != 0 || usaOffset + (usaCount * sizeof(ushort)) > record.Length)
        {
            return false;
        }

        int stride = record.Length / (usaCount - 1);
        if (stride * (usaCount - 1) != record.Length || stride < sizeof(ushort) || usaOffset + (usaCount * sizeof(ushort)) > stride - sizeof(ushort))
        {
            return false;
        }

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * stride) - sizeof(ushort);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[tail..]) != sequence)
            {
                return false;
            }
        }

        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * stride) - sizeof(ushort);
            record.Slice(usaOffset + (i * sizeof(ushort)), sizeof(ushort)).CopyTo(record[tail..]);
        }

        return true;
    }

    public static bool TryDecodeRuns(ReadOnlySpan<byte> pairs, long lowestVcn, List<Extent> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        long vcn = lowestVcn;
        long lcn = 0;
        int i = 0;
        while (i < pairs.Length)
        {
            byte header = pairs[i++];
            if (header == 0)
            {
                return true;
            }

            int lengthBytes = header & 0x0F;
            int offsetBytes = header >> 4;
            if (lengthBytes == 0 || lengthBytes > sizeof(long) || offsetBytes > sizeof(long) || i + lengthBytes + offsetBytes > pairs.Length)
            {
                return false;
            }

            long length = ReadUnsigned(pairs.Slice(i, lengthBytes));
            i += lengthBytes;
            if (length <= 0)
            {
                return false;
            }

            if (offsetBytes == 0)
            {
                runs.Add(new Extent(vcn, -1, length));
            }
            else
            {
                lcn += ReadSigned(pairs.Slice(i, offsetBytes));
                i += offsetBytes;
                if (lcn < 0)
                {
                    return false;
                }

                runs.Add(new Extent(vcn, lcn, length));
            }

            vcn += length;
        }

        return false;
    }

    public static Extent[]? Join(IReadOnlyList<StreamPiece> pieces, StreamKind kind, long bytesPerCluster, out long dataBytes)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        dataBytes = 0;
        if (bytesPerCluster <= 0)
        {
            return null;
        }

        int matching = 0;
        StreamPiece only = default;
        foreach (StreamPiece piece in pieces)
        {
            if (piece.Kind == kind)
            {
                matching++;
                only = piece;
            }
        }

        if (matching == 1)
        {
            if (only.Runs is null || only.LowestVcn != 0 || !Covers(only, only.HighestVcn + 1, bytesPerCluster))
            {
                return null;
            }

            dataBytes = only.DataBytes;
            return only.Runs;
        }

        StreamPiece[] chosen = [.. pieces.Where(p => p.Kind == kind).OrderBy(p => p.LowestVcn)];
        if (chosen.Length == 0 || chosen[0].LowestVcn != 0)
        {
            return null;
        }

        long next = 0;
        int count = 0;
        foreach (StreamPiece piece in chosen)
        {
            if (piece.Runs is null || piece.LowestVcn != next)
            {
                return null;
            }

            next = piece.HighestVcn + 1;
            count += piece.Runs.Length;
        }

        if (!Covers(chosen[0], next, bytesPerCluster))
        {
            return null;
        }

        dataBytes = chosen[0].DataBytes;
        var joined = new Extent[count];
        int at = 0;
        foreach (StreamPiece piece in chosen)
        {
            piece.Runs!.CopyTo(joined, at);
            at += piece.Runs.Length;
        }

        return joined;
    }

    private static bool Covers(StreamPiece first, long clusters, long bytesPerCluster) =>
        first.AllocatedBytes >= 0 && first.AllocatedBytes % bytesPerCluster == 0 && clusters == first.AllocatedBytes / bytesPerCluster;

    internal static bool IsInUse(ReadOnlySpan<byte> record) =>
        record.Length >= HeaderBytes
        && BinaryPrimitives.ReadUInt32LittleEndian(record) == FileSignature
        && (BinaryPrimitives.ReadUInt16LittleEndian(record[FlagsField..]) & InUseFlag) != 0;

    internal static bool TryParse(ReadOnlySpan<byte> record, ParsedRecord parsed, List<Extent> scratch)
    {
        parsed.Reset();
        if (record.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature)
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[FlagsField..]);
        parsed.InUse = (flags & InUseFlag) != 0;
        parsed.IsDirectory = (flags & DirectoryFlag) != 0;
        parsed.Sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[SequenceField..]);
        parsed.BaseRecord = BinaryPrimitives.ReadInt64LittleEndian(record[BaseRecordField..]) & MftRecord.RecordMask;
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[BytesInUseField..]), (uint)record.Length);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[FirstAttributeField..]);
        int nameStart = -1, nameChars = 0;
        while (true)
        {
            if (offset < HeaderBytes || offset + sizeof(uint) > used)
            {
                return false;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == EndMarker)
            {
                break;
            }

            if (offset + AttributeHeaderBytes > used)
            {
                return false;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + LengthField)..]);
            if (length < AttributeHeaderBytes || length > used - offset)
            {
                return false;
            }

            ReadOnlySpan<byte> attribute = record.Slice(offset, (int)length);
            if (!Visit(attribute, offset, type, parsed, scratch, ref nameStart, ref nameChars))
            {
                return false;
            }

            offset += (int)length;
        }

        if (nameStart >= 0)
        {
            parsed.Name = Encoding.Unicode.GetString(record.Slice(nameStart, nameChars * sizeof(char)));
        }

        return true;
    }

    private static bool Visit(ReadOnlySpan<byte> attribute, int at, uint type, ParsedRecord parsed, List<Extent> scratch, ref int nameStart, ref int nameChars)
    {
        bool nonResident = attribute[NonResidentField] != 0;
        int nameLength = attribute[NameLengthField] * sizeof(char);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[NameOffsetField..]);
        if (nameLength > 0 && nameOffset + nameLength > attribute.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> name = nameLength > 0 ? attribute.Slice(nameOffset, nameLength) : default;
        switch (type)
        {
            case StandardInformation:
            {
                if (nonResident || !TryValue(attribute, out ReadOnlySpan<byte> value, out _) || value.Length < StandardInformationBytes)
                {
                    return false;
                }

                parsed.LastWrite = BinaryPrimitives.ReadInt64LittleEndian(value[LastWriteField..]);
                parsed.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(value[FileAttributesField..]);
                parsed.HasStandard = true;
                return true;
            }

            case AttributeList:
                parsed.HasAttributeList = true;
                return true;
            case FileName:
            {
                if (nonResident || !TryValue(attribute, out ReadOnlySpan<byte> value, out int valueAt) || value.Length < NameField)
                {
                    return false;
                }

                int chars = value[NameCharsField];
                byte space = value[NamespaceField];
                if (NameField + (chars * sizeof(char)) > value.Length)
                {
                    return false;
                }

                if (nameStart < 0 || (parsed.Namespace == DosNamespace && space != DosNamespace))
                {
                    nameStart = at + valueAt + NameField;
                    nameChars = chars;
                    parsed.Namespace = space;
                    parsed.Parent = BinaryPrimitives.ReadInt64LittleEndian(value[ParentField..]);
                }

                return true;
            }

            case Data when nameLength == 0:
                if (!nonResident)
                {
                    if (!TryValue(attribute, out ReadOnlySpan<byte> value, out _))
                    {
                        return false;
                    }

                    parsed.ResidentBytes = value.Length;
                    return true;
                }

                return AddPiece(attribute, StreamKind.Data, parsed, scratch);
            case IndexAllocation when nonResident && IsDirectoryIndex(name):
                return AddPiece(attribute, StreamKind.Index, parsed, scratch);
            default:
                return true;
        }
    }

    private static bool AddPiece(ReadOnlySpan<byte> attribute, StreamKind kind, ParsedRecord parsed, List<Extent> scratch)
    {
        if (attribute.Length < NonResidentHeaderBytes)
        {
            return false;
        }

        long lowest = BinaryPrimitives.ReadInt64LittleEndian(attribute[LowestVcnField..]);
        long highest = BinaryPrimitives.ReadInt64LittleEndian(attribute[HighestVcnField..]);
        int pairs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[PairsOffsetField..]);
        long allocated = BinaryPrimitives.ReadInt64LittleEndian(attribute[AllocatedField..]);
        long data = BinaryPrimitives.ReadInt64LittleEndian(attribute[DataSizeField..]);
        if (pairs < NonResidentHeaderBytes || pairs > attribute.Length || lowest < 0)
        {
            return false;
        }

        scratch.Clear();
        bool complete = TryDecodeRuns(attribute[pairs..], lowest, scratch);
        long clusters = 0;
        foreach (Extent run in scratch)
        {
            clusters += run.Clusters;
        }

        complete &= clusters == highest - lowest + 1;
        parsed.Pieces.Add(new StreamPiece(kind, lowest, highest, allocated, data, complete ? [.. scratch] : null));
        return true;
    }

    private static bool TryValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value, out int valueAt)
    {
        value = default;
        valueAt = 0;
        if (attribute.Length < ResidentHeaderBytes)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[ValueLengthField..]);
        valueAt = BinaryPrimitives.ReadUInt16LittleEndian(attribute[ValueOffsetField..]);
        if (valueAt > attribute.Length || length > (uint)(attribute.Length - valueAt))
        {
            return false;
        }

        value = attribute.Slice(valueAt, (int)length);
        return true;
    }

    private static bool IsDirectoryIndex(ReadOnlySpan<byte> name)
    {
        if (name.Length != DirectoryIndex.Length * sizeof(char))
        {
            return false;
        }

        for (int i = 0; i < DirectoryIndex.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(name[(i * sizeof(char))..]) != DirectoryIndex[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool Visit(Span<byte> record, long number, long cluster, ParsedRecord parsed, List<Extent> scratch, Action<FileRecord> emit, Dictionary<long, Pending> merge)
    {
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (signature != FileSignature)
        {
            return signature == 0;
        }

        if (!IsInUse(record))
        {
            return true;
        }

        if (!TryFixup(record) || !TryParse(record, parsed, scratch))
        {
            return false;
        }

        if (parsed.BaseRecord != 0)
        {
            Pending extension = Pending.For(merge, parsed.BaseRecord);
            extension.Pieces.AddRange(parsed.Pieces);
            extension.Adopt(parsed);
            return true;
        }

        if (!parsed.HasStandard || (parsed.Name is null && !parsed.HasAttributeList))
        {
            return false;
        }

        var attributes = (FileAttributes)parsed.Attributes;
        attributes = parsed.IsDirectory ? attributes | FileAttributes.Directory : attributes & ~FileAttributes.Directory;
        long id = number | ((long)parsed.Sequence << 48);
        DateTime written = DateTime.FromFileTimeUtc(parsed.LastWrite is >= 0 and <= LargestFileTime ? parsed.LastWrite : 0);
        var entry = new MftRecord(id, parsed.Parent, attributes, parsed.Name ?? string.Empty);
        if (parsed.HasAttributeList)
        {
            Pending owner = Pending.For(merge, number);
            owner.Base = new FileRecord(entry, 0, written, null);
            owner.Pieces.AddRange(parsed.Pieces);
            owner.Adopt(parsed);
            return true;
        }

        Extent[]? extents = Streams(parsed, cluster, out long size);
        emit(new FileRecord(entry, size, written, extents));
        return true;
    }

    private static Extent[]? Streams(ParsedRecord parsed, long cluster, out long size)
    {
        size = 0;
        if (parsed.IsDirectory)
        {
            return parsed.Pieces.Count == 0 ? [] : Join(parsed.Pieces, StreamKind.Index, cluster, out _);
        }

        if (parsed.ResidentBytes >= 0)
        {
            size = parsed.ResidentBytes;
            return [];
        }

        return Join(parsed.Pieces, StreamKind.Data, cluster, out size);
    }

    private static int Finish(Action<FileRecord> emit, Dictionary<long, Pending> merge, long cluster)
    {
        int damaged = 0;
        foreach (long number in merge.Keys.Order())
        {
            Pending pending = merge[number];
            if (pending.Base is not { } record)
            {
                continue;
            }

            string name = record.Entry.Name.Length > 0 ? record.Entry.Name : pending.Name ?? string.Empty;
            long parent = record.Entry.Name.Length > 0 ? record.Entry.Parent : pending.Parent;
            long size = 0;
            Extent[]? extents;
            if (record.Entry.IsDirectory)
            {
                extents = pending.Pieces.Exists(p => p.Kind == StreamKind.Index) ? Join(pending.Pieces, StreamKind.Index, cluster, out _) : [];
            }
            else if (pending.ResidentBytes >= 0)
            {
                size = pending.ResidentBytes;
                extents = [];
            }
            else
            {
                extents = Join(pending.Pieces, StreamKind.Data, cluster, out size);
            }

            if (name.Length == 0)
            {
                damaged++;
                continue;
            }

            emit(record with { Entry = record.Entry with { Name = name, Parent = parent }, Size = size, Extents = extents });
        }

        return damaged;
    }

    private static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;

    private static long ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return (long)value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[^1];
        for (int i = bytes.Length - 2; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    internal sealed class ParsedRecord
    {
        public bool InUse { get; set; }

        public bool IsDirectory { get; set; }

        public ushort Sequence { get; set; }

        public long BaseRecord { get; set; }

        public bool HasStandard { get; set; }

        public bool HasAttributeList { get; set; }

        public long LastWrite { get; set; }

        public uint Attributes { get; set; }

        public string? Name { get; set; }

        public long Parent { get; set; }

        public byte Namespace { get; set; }

        public long ResidentBytes { get; set; } = -1;

        public List<StreamPiece> Pieces { get; } = [];

        public void Reset()
        {
            InUse = IsDirectory = HasStandard = HasAttributeList = false;
            Sequence = 0;
            BaseRecord = LastWrite = Parent = 0;
            Attributes = 0;
            Name = null;
            Namespace = 0;
            ResidentBytes = -1;
            Pieces.Clear();
        }
    }

    private sealed class Pending
    {
        public FileRecord? Base { get; set; }

        public List<StreamPiece> Pieces { get; } = [];

        public long ResidentBytes { get; set; } = -1;

        public string? Name { get; private set; }

        public long Parent { get; private set; }

        private byte Namespace { get; set; }

        public static Pending For(Dictionary<long, Pending> merge, long number)
        {
            if (!merge.TryGetValue(number, out Pending? pending))
            {
                pending = new Pending();
                merge[number] = pending;
            }

            return pending;
        }

        public void Adopt(ParsedRecord parsed)
        {
            if (parsed.ResidentBytes >= 0)
            {
                ResidentBytes = parsed.ResidentBytes;
            }

            if (parsed.Name is not null && (Name is null || (Namespace == DosNamespace && parsed.Namespace != DosNamespace)))
            {
                Name = parsed.Name;
                Parent = parsed.Parent;
                Namespace = parsed.Namespace;
            }
        }
    }
}
