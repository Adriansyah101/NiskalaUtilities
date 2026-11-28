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

    public void DecodeInto(ReadOnlySpan<byte> bytes, ConsoleLineAssembler assembler, List<ConsoleLine> lines) =>
        DecodeInto(bytes, flush: false, assembler, lines);

    public void FinishInto(ConsoleLineAssembler assembler, List<ConsoleLine> lines) =>
        DecodeInto([], flush: true, assembler, lines);

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
        var text = new StringBuilder();
        Decode(bytes, flush, text, static (chars, sink) => sink.Append(chars));
        return text.ToString();
    }

    private void DecodeInto(ReadOnlySpan<byte> bytes, bool flush, ConsoleLineAssembler assembler, List<ConsoleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        Decode(bytes, flush, (assembler, lines), static (chars, sink) => sink.assembler.Append(chars, sink.lines));
    }

    private void Decode<TSink>(ReadOnlySpan<byte> bytes, bool flush, TSink sink, CharSink<TSink> emit)
    {
        if (_decoder is not null)
        {
            Convert(bytes, flush, sink, emit);
            return;
        }

        _held.AddRange(bytes);
        if ((!flush && (_held.Count < DetectionBytes || IsPartialUtf8Mark(_held))) || _held.Count == 0)
        {
            return;
        }

        byte[] sample = ArrayPool<byte>.Shared.Rent(_held.Count);
        int length = _held.Count;
        try
        {
            _held.CopyTo(sample);
            _held.Clear();
            ReadOnlySpan<byte> head = sample.AsSpan(0, length);
            Detected = Detect(head);
            _decoder = Choose(Detected).GetDecoder();
            Convert(Skip(head, Detected), flush, sink, emit);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sample);
        }
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

    private void Convert<TSink>(ReadOnlySpan<byte> bytes, bool flush, TSink sink, CharSink<TSink> emit)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, _decoder!.GetCharCount(bytes, flush)));
        try
        {
            int written = _decoder.GetChars(bytes, chars, flush);
            if (written > 0)
            {
                emit(chars.AsSpan(0, written), sink);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private delegate void CharSink<in TSink>(ReadOnlySpan<char> chars, TSink sink);

    private static bool IsPartialUtf8Mark(List<byte> held)
    {
        if (held.Count >= Utf8Mark.Length)
        {
            return false;
        }

        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] != Utf8Mark[i])
            {
                return false;
            }
        }

        return true;
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
