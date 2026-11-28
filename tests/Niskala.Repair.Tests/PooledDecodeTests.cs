using System.Text;
using Niskala.Repair.Output;

namespace Niskala.Repair.Tests;

public sealed class PooledDecodeTests
{
    private const string Scan =
        "\r\nBeginning system scan.\r\n" +
        "Verification 1% complete.\rVerification 57% complete.\rVerification 100% complete.\r\r\n" +
        "Windows Resource Protection did not find any integrity violations.\r\n";

    public static TheoryData<int> Chunks => [1, 2, 3, 7, 64, 4096];

    [Theory]
    [MemberData(nameof(Chunks))]
    public void Decoding_straight_into_lines_matches_decoding_to_text_first(int chunk)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(Scan);

        Assert.Equal(ThroughText(bytes, chunk), Direct(bytes, chunk));
    }

    [Theory]
    [MemberData(nameof(Chunks))]
    public void Utf8_with_a_mark_split_anywhere_decodes_the_same(int chunk)
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Naïve café: 100%\r\nDone\r\n")];

        List<ConsoleLine> lines = Direct(bytes, chunk);

        Assert.Equal(["Naïve café: 100%", "Done"], lines.Where(l => l.IsFinal).Select(l => l.Text));
    }

    [Fact]
    public void A_single_byte_is_held_until_the_end()
    {
        var decoder = new ConsoleTextDecoder(437);
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();

        decoder.DecodeInto([(byte)'O'], assembler, lines);
        Assert.Empty(lines);

        decoder.FinishInto(assembler, lines);
        Assert.Equal("O", Assert.Single(lines).Text);
    }

    private static List<ConsoleLine> Direct(byte[] bytes, int chunk)
    {
        var decoder = new ConsoleTextDecoder(437);
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        for (int i = 0; i < bytes.Length; i += chunk)
        {
            decoder.DecodeInto(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)), assembler, lines);
        }

        decoder.FinishInto(assembler, lines);
        if (assembler.Complete() is { } last)
        {
            lines.Add(last);
        }

        return lines;
    }

    private static List<ConsoleLine> ThroughText(byte[] bytes, int chunk)
    {
        var decoder = new ConsoleTextDecoder(437);
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        for (int i = 0; i < bytes.Length; i += chunk)
        {
            assembler.Append(decoder.Decode(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i))), lines);
        }

        assembler.Append(decoder.Finish(), lines);
        if (assembler.Complete() is { } last)
        {
            lines.Add(last);
        }

        return lines;
    }
}
