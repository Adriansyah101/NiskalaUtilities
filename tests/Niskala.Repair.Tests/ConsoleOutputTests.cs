using System.Text;
using Niskala.Repair.Output;

namespace Niskala.Repair.Tests;

public sealed class ConsoleOutputTests
{
    private const string SfcScan =
        "\r\nBeginning system scan.  This process will take some time.\r\n\r\n" +
        "Beginning verification phase of system scan.\r\n" +
        "Verification 1% complete.\rVerification 2% complete.\rVerification 57% complete.\rVerification 100% complete.\r\r\n" +
        "\r\nWindows Resource Protection did not find any integrity violations.\r\n";

    private const string DismRestore =
        "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1\r\n\r\n" +
        "\r[==                         4.3%                           ] " +
        "\r[===========                20.0%                          ] " +
        "\r[==========================100.0%==========================] \r\n" +
        "The restore operation completed successfully.\r\n";

    [Fact]
    public void Sfc_output_without_a_mark_is_read_as_utf16()
    {
        byte[] bytes = Encoding.Unicode.GetBytes(SfcScan);
        var decoder = new ConsoleTextDecoder(437);
        string text = decoder.Decode(bytes) + decoder.Finish();
        Assert.Equal(ConsoleEncoding.Utf16LittleEndian, decoder.Detected);
        Assert.Equal(SfcScan, text);
    }

    [Fact]
    public void A_utf16_mark_is_dropped()
    {
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("ok\r\n")];
        var decoder = new ConsoleTextDecoder(437);
        Assert.Equal("ok\r\n", decoder.Decode(bytes) + decoder.Finish());
        Assert.Equal(ConsoleEncoding.Utf16LittleEndian, decoder.Detected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Utf16_split_at_odd_offsets_decodes_the_same(int chunk)
    {
        byte[] bytes = Encoding.Unicode.GetBytes("Verifikasi 45% selesai. ¿Ñandú?\r\n");
        Assert.Equal(Encoding.Unicode.GetString(bytes), Feed(new ConsoleTextDecoder(437), bytes, chunk));
    }

    [Theory]
    [InlineData(437, "Verificación 50% completada.")]
    [InlineData(850, "Überprüfung zu 50 % abgeschlossen.")]
    [InlineData(866, "Проверка 50% завершена.")]
    public void Oem_output_uses_the_system_oem_code_page(int codePage, string expected)
    {
        byte[] bytes = ConsoleTextDecoder.OemEncoding(codePage).GetBytes(expected + "\r\n");
        var decoder = new ConsoleTextDecoder(codePage);
        Assert.Equal(expected + "\r\n", decoder.Decode(bytes) + decoder.Finish());
        Assert.Equal(ConsoleEncoding.OemCodePage, decoder.Detected);
    }

    [Fact]
    public void Plain_ascii_goes_through_the_oem_code_page()
    {
        var decoder = new ConsoleTextDecoder(437);
        Assert.Equal("Windows IP Configuration\r\n", decoder.Decode("Windows IP Configuration\r\n"u8) + decoder.Finish());
        Assert.Equal(ConsoleEncoding.OemCodePage, decoder.Detected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Utf8_output_is_recognised_even_when_split_inside_a_character(int chunk)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Überprüfung 18,0 % — fertig\r\n");
        var decoder = new ConsoleTextDecoder(437);
        Assert.Equal(Encoding.UTF8.GetString(bytes), Feed(decoder, bytes, chunk));
        Assert.Equal(ConsoleEncoding.Utf8, decoder.Detected);
    }

    [Fact]
    public void A_utf8_console_code_page_decodes_as_utf8()
    {
        Assert.Equal(65001, ConsoleTextDecoder.OemEncoding(65001).CodePage);
        Assert.Equal(ConsoleEncoding.Utf8, ConsoleTextDecoder.Detect([0xEF, 0xBB, 0xBF, (byte)'a']));
    }

    [Fact]
    public void A_single_byte_waits_for_the_next_one_before_choosing()
    {
        var decoder = new ConsoleTextDecoder(437);
        Assert.Equal(string.Empty, decoder.Decode([(byte)'O']));
        Assert.Equal(ConsoleEncoding.Undetected, decoder.Detected);
        Assert.Equal("O", decoder.Finish());
        Assert.Equal(ConsoleEncoding.OemCodePage, decoder.Detected);
    }

    [Fact]
    public void Sfc_progress_updates_in_place_and_only_the_last_one_stays()
    {
        List<ConsoleLine> lines = Assemble(SfcScan, 1);
        Assert.Equal(
            ["", "Beginning system scan.  This process will take some time.", "", "Beginning verification phase of system scan.", "Verification 100% complete.", "", "Windows Resource Protection did not find any integrity violations."],
            lines.Where(l => l.IsFinal).Select(l => l.Text));
        Assert.Equal([1.0, 2, 57, 100], Percents(lines));
    }

    [Fact]
    public void Dism_progress_bars_are_read_as_they_are_redrawn()
    {
        List<ConsoleLine> lines = Assemble(DismRestore, 16);
        Assert.Equal([4.3, 20, 100], Percents(lines));
        Assert.Contains(lines, l => l is { IsFinal: true, Text: "The restore operation completed successfully." });
        Assert.Contains(lines, l => l.IsFinal && l.Text.StartsWith("[====", StringComparison.Ordinal) && l.Text.Contains("100.0%", StringComparison.Ordinal));
    }

    [Fact]
    public void A_carriage_return_before_a_line_feed_keeps_the_text()
    {
        List<ConsoleLine> lines = Assemble("Done.\r\r\n", 64);
        Assert.Equal([new ConsoleLine("Done.", true)], lines);
    }

    [Fact]
    public void A_line_cut_by_a_chunk_is_shown_live_and_then_finished()
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        assembler.Append("Verification 4", lines);
        assembler.Append("2% complete.\r\n", lines);
        Assert.Equal([new ConsoleLine("Verification 4", false), new ConsoleLine("Verification 42% complete.", true)], lines);
    }

    [Fact]
    public void Text_after_the_last_line_feed_is_completed_at_the_end()
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        assembler.Append("a\r\nb", lines);
        Assert.Equal(new ConsoleLine("b", true), assembler.Complete());
        Assert.Null(assembler.Complete());
    }

    [Theory]
    [InlineData("Verification 45% complete.", 45.0)]
    [InlineData("Verifikasi 7% selesai.", 7.0)]
    [InlineData("[==========                 18.0%                          ]", 18.0)]
    [InlineData("[==         5,3%          ]", 5.3)]
    [InlineData("Vérification 5 % effectuée.", 5.0)]
    [InlineData("Progress: 12345 of 67890 done; Stage: 18%; Total: 5%; ETA: 0:01:02", 5.0)]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    public void Percentages_are_read_from_progress_text(string text, double expected)
    {
        Assert.Equal(expected, RepairProgressParser.TryParse(text));
    }

    [Theory]
    [InlineData("The operation completed successfully.")]
    [InlineData("Usage 150% of the quota")]
    [InlineData("1000%")]
    [InlineData("Version 10.0.26100.1")]
    public void Text_without_a_valid_percentage_gives_nothing(string text)
    {
        Assert.Null(RepairProgressParser.TryParse(text));
    }

    private static string Feed(ConsoleTextDecoder decoder, byte[] bytes, int chunk)
    {
        var text = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += chunk)
        {
            text.Append(decoder.Decode(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i))));
        }

        return text.Append(decoder.Finish()).ToString();
    }

    private static List<ConsoleLine> Assemble(string text, int chunk)
    {
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        for (int i = 0; i < text.Length; i += chunk)
        {
            assembler.Append(text.AsSpan(i, Math.Min(chunk, text.Length - i)), lines);
        }

        if (assembler.Complete() is { } last)
        {
            lines.Add(last);
        }

        return lines;
    }

    private static double[] Percents(List<ConsoleLine> lines) =>
        [.. lines.Select(l => RepairProgressParser.TryParse(l.Text)).OfType<double>().Distinct()];
}
