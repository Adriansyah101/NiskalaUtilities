using System.Buffers.Binary;
using System.Text;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

internal sealed class MftImage
{
    public const int Sector = 512;
    public const ushort Stamp = 0x0B0B;

    private readonly byte[] _volume;
    private readonly (long Lcn, long Clusters)[] _runs;

    public MftImage(long clusters, int cluster, int record, int slots, params (long Lcn, long Clusters)[] runs)
    {
        Cluster = cluster;
        RecordBytes = record;
        Slots = slots;
        _runs = runs;
        _volume = new byte[clusters * cluster];
        Assert.Equal((long)slots * record, runs.Sum(r => r.Clusters) * cluster);
    }

    public int Cluster { get; }

    public int RecordBytes { get; }

    public int Slots { get; }

    public int Reads { get; private set; }

    public int FailAfter { get; set; } = -1;

    public int FailWith { get; set; }

    public MftGeometry Geometry => new(Cluster, Sector, RecordBytes, _runs[0].Lcn, (long)Slots * RecordBytes);

    public (long Lcn, long Clusters)[] Runs => _runs;

    public MftImage Put(long number, byte[] record)
    {
        Assert.Equal(RecordBytes, record.Length);
        long at = number * RecordBytes;
        for (int i = 0; i < record.Length; i++)
        {
            _volume[Offset(at + i)] = record[i];
        }

        return this;
    }

    public MftImage Tear(long number, int sector)
    {
        _volume[Offset((number * RecordBytes) + ((sector + 1) * Sector) - 2)] ^= 0xFF;
        return this;
    }

    public int Read(long offset, Span<byte> buffer, out int read)
    {
        read = 0;
        Reads++;
        if (FailAfter >= 0 && Reads > FailAfter)
        {
            return FailWith;
        }

        Assert.Equal(0, offset % Sector);
        Assert.Equal(0, buffer.Length % Sector);
        int count = (int)Math.Min(buffer.Length, _volume.Length - offset);
        _volume.AsSpan((int)offset, count).CopyTo(buffer);
        read = count;
        return 0;
    }

    public List<FileRecord> ReadAll(out int error, out int damaged)
    {
        var records = new List<FileRecord>();
        error = NtfsMftRecordReader.Read(Geometry, Read, records.Add, null, CancellationToken.None, out damaged);
        return records;
    }

    private long Offset(long streamByte)
    {
        long cluster = streamByte / Cluster;
        foreach ((long lcn, long clusters) in _runs)
        {
            if (cluster < clusters)
            {
                return ((lcn + cluster) * Cluster) + (streamByte % Cluster);
            }

            cluster -= clusters;
        }

        throw new ArgumentOutOfRangeException(nameof(streamByte));
    }
}

internal sealed class RecordBuilder(ushort sequence, bool inUse = true, bool directory = false, long baseRecord = 0)
{
    public const int UsaOffset = 0x30;
    public const int FirstAttribute = 0x38;
    public const byte Posix = 0;
    public const byte Win32 = 1;
    public const byte Dos = 2;

    private readonly List<byte[]> _attributes = [];

    public static long Id(long number, ushort sequence) => number | ((long)sequence << 48);

    public RecordBuilder Standard(DateTime written, FileAttributes attributes = FileAttributes.Archive)
    {
        var value = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x08), written.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x20), (uint)attributes);
        return Resident(NtfsMftRecordReader.StandardInformation, string.Empty, value);
    }

    public RecordBuilder Name(long parent, string name, byte space = Win32)
    {
        byte[] text = Encoding.Unicode.GetBytes(name);
        var value = new byte[0x42 + text.Length];
        BinaryPrimitives.WriteInt64LittleEndian(value, parent);
        value[0x40] = (byte)name.Length;
        value[0x41] = space;
        text.CopyTo(value.AsSpan(0x42));
        return Resident(NtfsMftRecordReader.FileName, string.Empty, value);
    }

    public RecordBuilder ResidentData(int bytes, string stream = "") => Resident(NtfsMftRecordReader.Data, stream, new byte[bytes]);

    public RecordBuilder AttributeList() => Resident(NtfsMftRecordReader.AttributeList, string.Empty, new byte[0x20]);

    public RecordBuilder Data(long lowestVcn, long highestVcn, long allocated, long size, params (long Lcn, long Clusters)[] runs) =>
        NonResident(NtfsMftRecordReader.Data, string.Empty, lowestVcn, highestVcn, allocated, size, runs);

    public RecordBuilder Index(long allocated, params (long Lcn, long Clusters)[] runs) =>
        NonResident(NtfsMftRecordReader.IndexAllocation, NtfsMftRecordReader.DirectoryIndex, 0, runs.Sum(r => r.Clusters) - 1, allocated, allocated, runs);

    public RecordBuilder NonResident(uint type, string stream, long lowestVcn, long highestVcn, long allocated, long size, params (long Lcn, long Clusters)[] runs)
    {
        byte[] name = Encoding.Unicode.GetBytes(stream);
        byte[] pairs = Pairs(runs);
        int pairsAt = Align(0x40 + name.Length);
        var attribute = new byte[Align(pairsAt + pairs.Length)];
        Header(attribute, type, nonResident: true, stream.Length, 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x18), highestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), (ushort)pairsAt);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x28), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x38), size);
        name.CopyTo(attribute.AsSpan(0x40));
        pairs.CopyTo(attribute.AsSpan(pairsAt));
        _attributes.Add(attribute);
        return this;
    }

    public byte[] Build(int recordBytes)
    {
        var record = new byte[recordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(record, NtfsMftRecordReader.FileSignature);
        int sectors = recordBytes / MftImage.Sector;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04), UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x06), (ushort)(sectors + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x10), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), FirstAttribute);
        ushort flags = (ushort)((inUse ? NtfsMftRecordReader.InUseFlag : 0) | (directory ? NtfsMftRecordReader.DirectoryFlag : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16), flags);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0x20), baseRecord);
        int at = FirstAttribute;
        foreach (byte[] attribute in _attributes)
        {
            attribute.CopyTo(record.AsSpan(at));
            at += attribute.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(at), NtfsMftRecordReader.EndMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), (uint)(at + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x1C), (uint)recordBytes);
        Assert.True(at + 8 <= recordBytes - 2, "the attributes do not fit the record");
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UsaOffset), MftImage.Stamp);
        for (int i = 1; i <= sectors; i++)
        {
            int tail = (i * MftImage.Sector) - 2;
            record.AsSpan(tail, 2).CopyTo(record.AsSpan(UsaOffset + (i * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), MftImage.Stamp);
        }

        return record;
    }

    public static byte[] Pairs(params (long Lcn, long Clusters)[] runs)
    {
        var pairs = new List<byte>();
        long previous = 0;
        foreach ((long lcn, long clusters) in runs)
        {
            byte[] length = Bytes(clusters);
            byte[] offset = lcn < 0 ? [] : Bytes(lcn - previous);
            pairs.Add((byte)(length.Length | (offset.Length << 4)));
            pairs.AddRange(length);
            pairs.AddRange(offset);
            if (lcn >= 0)
            {
                previous = lcn;
            }
        }

        pairs.Add(0);
        return [.. pairs];
    }

    private static byte[] Bytes(long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Add((byte)value);
            value >>= 8;
        }
        while (!(value == 0 && (bytes[^1] & 0x80) == 0) && !(value == -1 && (bytes[^1] & 0x80) != 0));

        return [.. bytes];
    }

    private static int Align(int value) => (value + 7) / 8 * 8;

    private static void Header(byte[] attribute, uint type, bool nonResident, int nameChars, int nameAt)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)attribute.Length);
        attribute[8] = nonResident ? (byte)1 : (byte)0;
        attribute[9] = (byte)nameChars;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), (ushort)nameAt);
    }

    private RecordBuilder Resident(uint type, string stream, byte[] value)
    {
        byte[] name = Encoding.Unicode.GetBytes(stream);
        int valueAt = Align(0x18 + name.Length);
        var attribute = new byte[Align(valueAt + value.Length)];
        Header(attribute, type, nonResident: false, stream.Length, 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x14), (ushort)valueAt);
        name.CopyTo(attribute.AsSpan(0x18));
        value.CopyTo(attribute.AsSpan(valueAt));
        _attributes.Add(attribute);
        return this;
    }
}
