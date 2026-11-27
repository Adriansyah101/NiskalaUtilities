using Niskala.Repair.Output;

namespace Niskala.Repair.Tests;

public sealed class ConsoleTranscriptTests
{
    [Fact]
    public void A_carriage_return_overwrites_the_progress_line_in_place()
    {
        var transcript = new ConsoleTranscript();
        transcript.Echo("> Dism.exe /Online /Cleanup-Image /RestoreHealth");

        TranscriptChange opened = transcript.Apply(Live("[==                         4.3%                           ]"));
        TranscriptChange moved = transcript.Apply(Live("[==========================69.6%====                       ]"));
        TranscriptChange closed = transcript.Apply(Final("[==========================100.0%==========================]"));
        transcript.Apply(Final("The restore operation completed successfully."));

        Assert.Equal(TranscriptAction.Append, opened.Action);
        Assert.Equal(TranscriptAction.Replace, moved.Action);
        Assert.Equal(opened.Index, moved.Index);
        Assert.Equal(TranscriptAction.Replace, closed.Action);
        Assert.Equal(opened.Index, closed.Index);
        Assert.Equal(
            ["> Dism.exe /Online /Cleanup-Image /RestoreHealth", "[==========================100.0%==========================]", "The restore operation completed successfully."],
            transcript.Lines.Select(l => l.Text));
        Assert.All(transcript.Lines, l => Assert.False(l.IsOpen));
        Assert.True(transcript.Lines[0].IsCommand);
    }

    [Fact]
    public void Real_dism_output_through_the_runner_leaves_one_progress_line()
    {
        var transcript = new ConsoleTranscript();
        var assembler = new ConsoleLineAssembler();
        var lines = new List<ConsoleLine>();
        assembler.Append(
            "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1\r\n\r\nImage Version: 10.0.26100.2894\r\n\r\n" +
            "\r[==                         4.3%                           ] \r[===========                20.0%                          ] " +
            "\r[==========================69.6%====                       ] \r[==========================100.0%==========================] \r\n" +
            "The restore operation completed successfully.\r\n",
            lines);

        foreach (ConsoleLine line in lines)
        {
            transcript.Apply(new RepairUpdate(RepairStream.Output, line.Text, line.IsFinal, null));
        }

        Assert.Single(transcript.Lines, l => l.Text.Contains('%', StringComparison.Ordinal));
        Assert.Contains(transcript.Lines, l => l.Text.StartsWith("[==========================100.0%", StringComparison.Ordinal));
        Assert.Equal("The restore operation completed successfully.", transcript.Lines[^1].Text);
    }

    [Fact]
    public void Progress_lines_ended_by_newlines_collapse_into_one()
    {
        var transcript = new ConsoleTranscript();

        transcript.Apply(Final("Beginning verification phase of system scan."));
        transcript.Apply(Final("Verification 5% complete."));
        TranscriptChange second = transcript.Apply(Final("Verification 57% complete."));
        transcript.Apply(Final("Verification 100% complete."));
        transcript.Apply(Final("Windows Resource Protection did not find any integrity violations."));

        Assert.Equal(TranscriptAction.Replace, second.Action);
        Assert.Equal(
            ["Beginning verification phase of system scan.", "Verification 100% complete.", "Windows Resource Protection did not find any integrity violations."],
            transcript.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Chkdsk_stage_progress_stays_on_one_line_per_stage()
    {
        var transcript = new ConsoleTranscript();

        transcript.Apply(Final("Stage 1: Examining basic file system structure ..."));
        transcript.Apply(Live("  Progress: 0 of 812544 done; Stage:  0%; Total:  0%; ETA:   0:01:10    "));
        transcript.Apply(Live("  Progress: 406272 of 812544 done; Stage: 50%; Total: 16%; ETA:   0:00:40    "));
        transcript.Apply(Final("  812544 file records processed."));
        transcript.Apply(Final("Stage 2: Examining file name linkage ..."));
        transcript.Apply(Live("  Progress: 900000 of 1100000 done; Stage: 81%; Total: 47%; ETA:   0:00:21 .  "));
        transcript.Apply(Final("  1100000 index entries processed."));

        Assert.Equal(
            ["Stage 1: Examining basic file system structure ...", "  812544 file records processed.", "Stage 2: Examining file name linkage ...", "  1100000 index entries processed."],
            transcript.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Ordinary_lines_are_never_folded_together()
    {
        var transcript = new ConsoleTranscript();

        transcript.Apply(Final("Windows IP Configuration"));
        transcript.Apply(Final(string.Empty));
        transcript.Apply(Final(string.Empty));
        transcript.Apply(Final("Successfully flushed the DNS Resolver Cache."));
        transcript.Apply(Final("Successfully flushed the DNS Resolver Cache."));

        Assert.Equal(
            ["Windows IP Configuration", string.Empty, "Successfully flushed the DNS Resolver Cache.", "Successfully flushed the DNS Resolver Cache."],
            transcript.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Each_stream_keeps_its_own_open_line()
    {
        var transcript = new ConsoleTranscript();

        TranscriptChange output = transcript.Apply(Live("Verification 10% complete."));
        transcript.Apply(new RepairUpdate(RepairStream.Error, "warning: a file is in use", IsFinal: true, null));
        TranscriptChange later = transcript.Apply(Live("Verification 20% complete."));

        Assert.Equal(output.Index, later.Index);
        Assert.Equal(2, transcript.Lines.Count);
        Assert.Equal(RepairStream.Error, transcript.Lines[1].Stream);
    }

    [Fact]
    public void A_full_transcript_drops_the_oldest_output_but_keeps_the_command()
    {
        var transcript = new ConsoleTranscript(capacity: 3);
        transcript.Echo("> netsh.exe winsock reset");
        transcript.Apply(Final("one"));
        transcript.Apply(Final("two"));

        TranscriptChange change = transcript.Apply(Final("three"));

        Assert.Equal(1, change.Dropped);
        Assert.Equal(["> netsh.exe winsock reset", "two", "three"], transcript.Lines.Select(l => l.Text));
        Assert.Equal(2, change.Index);
    }

    [Theory]
    [InlineData("[==   4.3%   ]", "[======  70.1%==   ]")]
    [InlineData("Verification 5% complete.", "Verification 100% complete.")]
    [InlineData("Überprüfung 18,0 % abgeschlossen.", "Überprüfung 99,5 % abgeschlossen.")]
    public void Progress_lines_of_one_tool_share_a_shape(string first, string second)
    {
        Assert.NotNull(ConsoleTranscript.ProgressShape(first));
        Assert.Equal(ConsoleTranscript.ProgressShape(first), ConsoleTranscript.ProgressShape(second));
        Assert.Null(ConsoleTranscript.ProgressShape("The operation completed successfully."));
    }

    private static RepairUpdate Live(string text) => new(RepairStream.Output, text, IsFinal: false, null);

    private static RepairUpdate Final(string text) => new(RepairStream.Output, text, IsFinal: true, null);
}
