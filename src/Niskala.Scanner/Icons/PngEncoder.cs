using System.Buffers.Binary;
using System.IO.Compression;
using Niskala.Interop.Icons;

namespace Niskala.Scanner.Icons;

public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Write(Stream output, IconPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pixels);

        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], pixels.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], pixels.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);

        WriteChunk(output, "IDAT"u8, Compress(pixels));
        WriteChunk(output, "IEND"u8, []);
    }

    private static byte[] Compress(IconPixels pixels)
    {
        int stride = pixels.Width * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] row = new byte[stride + 1];
            for (int y = 0; y < pixels.Height; y++)
            {
                ReadOnlySpan<byte> source = pixels.Bgra.AsSpan(y * stride, stride);
                for (int x = 0; x < stride; x += 4)
                {
                    row[x + 1] = source[x + 2];
                    row[x + 2] = source[x + 1];
                    row[x + 3] = source[x];
                    row[x + 4] = source[x + 3];
                }

                zlib.Write(row);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);

        uint crc = Update(Update(0xFFFF_FFFFu, type), data) ^ 0xFFFF_FFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
