using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Niskala.Repair.Output;

public enum ConsoleEncoding
{
    Undetected,
    Utf16LittleEndian,
    Utf8,
    OemCodePage,
}

internal sealed class ConsoleTextDecoder(int oemCodePage)
{
    private const int Utf8CodePage = 65001;
    private const int DetectionBytes = 2;

    private static readonly byte[] Utf16Mark = [0xFF, 0xFE];
    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];

    private readonly List<byte> _held = [];
    private Decoder? _decoder;

    public ConsoleEncoding Detected { get; private set; }

    public string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, flush: false);

    public string Finish() => Decode([], flush: true);

    internal static Encoding OemEncoding(int codePage) => codePage switch
    {
        Utf8CodePage => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        _ => CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage),
    };

    internal static ConsoleEncoding Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith(Utf16Mark) || sample.Contains((byte)0))
        {
            return ConsoleEncoding.Utf16LittleEndian;
        }

        if (sample.StartsWith(Utf8Mark))
        {
            return ConsoleEncoding.Utf8;
        }

        return IsMultibyteUtf8(sample) ? ConsoleEncoding.Utf8 : ConsoleEncoding.OemCodePage;
    }

    private string Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        if (_decoder is null)
        {
            _held.AddRange(bytes);
            if (_held.Count < DetectionBytes && !flush)
            {
                return string.Empty;
            }

            byte[] sample = [.. _held];
            _held.Clear();
            if (sample.Length == 0)
            {
                return string.Empty;
            }

            Detected = Detect(sample);
            _decoder = Choose(Detected).GetDecoder();
            return Convert(Skip(sample, Detected), flush);
        }

        return Convert(bytes, flush);
    }

    private Encoding Choose(ConsoleEncoding encoding) => encoding switch
    {
        ConsoleEncoding.Utf16LittleEndian => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        ConsoleEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        _ => OemEncoding(oemCodePage),
    };

    private static ReadOnlySpan<byte> Skip(ReadOnlySpan<byte> sample, ConsoleEncoding encoding) => encoding switch
    {
        ConsoleEncoding.Utf16LittleEndian when sample.StartsWith(Utf16Mark) => sample[Utf16Mark.Length..],
        ConsoleEncoding.Utf8 when sample.StartsWith(Utf8Mark) => sample[Utf8Mark.Length..],
        _ => sample,
    };

    private string Convert(ReadOnlySpan<byte> bytes, bool flush)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, _decoder!.GetCharCount(bytes, flush)));
        try
        {
            int written = _decoder.GetChars(bytes, chars, flush);
            return new string(chars, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static bool IsMultibyteUtf8(ReadOnlySpan<byte> sample)
    {
        if (!sample.ContainsAnyExceptInRange((byte)0, (byte)0x7F))
        {
            return false;
        }

        char[] chars = ArrayPool<char>.Shared.Rent(sample.Length);
        try
        {
            return Utf8.ToUtf16(sample, chars, out _, out _, replaceInvalidSequences: false, isFinalBlock: false) != OperationStatus.InvalidData;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
