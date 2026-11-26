namespace Niskala.Scanner.Detection;

public readonly record struct NsisProbeResult(bool IsNsis, bool IsUninstaller, long HeaderOffset);

public interface IExecutableProbe
{
    NsisProbeResult ProbeNsis(string executablePath);
}

public sealed class FileExecutableProbe : IExecutableProbe
{
    private const int MaxBytesScanned = 16 * 1024 * 1024;
    private const int Alignment = 512;
    private const int HeaderLength = 20;
    private const int FhFlagsUninstall = 1;

    private static ReadOnlySpan<byte> Magic => "NullsoftInst"u8;

    public static FileExecutableProbe Instance { get; } = new();

    public NsisProbeResult ProbeNsis(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            int length = (int)Math.Min(stream.Length, MaxBytesScanned);
            if (length < Alignment + HeaderLength)
            {
                return default;
            }

            byte[] data = new byte[length];
            stream.ReadExactly(data);
            return Scan(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    internal static NsisProbeResult Scan(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != 'M' || data[1] != 'Z')
        {
            return default;
        }

        for (int offset = Alignment; offset + HeaderLength <= data.Length; offset += Alignment)
        {
            ReadOnlySpan<byte> header = data.Slice(offset, HeaderLength);
            if (header[4] == 0xEF && header[5] == 0xBE && header[6] == 0xAD && header[7] == 0xDE
                && header.Slice(8, Magic.Length).SequenceEqual(Magic))
            {
                int flags = BitConverter.ToInt32(header[..4]);
                return new NsisProbeResult(true, (flags & FhFlagsUninstall) != 0, offset);
            }
        }

        return default;
    }
}
