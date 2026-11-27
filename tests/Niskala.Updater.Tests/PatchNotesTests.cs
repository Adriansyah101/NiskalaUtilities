using System.Xml.Linq;

namespace Niskala.Updater.Tests;

public sealed class PatchNotesTests
{
    private static readonly string[] RawSyntax = ["`", "**", "](", "| ", "|---", "<!--"];

    [Fact]
    public void Headings_bullets_and_paragraphs_keep_their_shape()
    {
        IReadOnlyList<NoteLine> lines = PatchNotes.Parse(
            "# Patch notes\r\n\r\nNewest first,\r\nwrapped.\r\n\r\n## v0.1.1 (2026-11-27)\r\n\r\n### Added\r\n\r\n- **Updates.** Run `niskala update`.\r\n  It verifies `SHA256SUMS.txt`.\r\n  * nested _item_\r\n1. First\r\n2) Second\r\n");

        Assert.Equal(
            [
                new NoteLine(NoteKind.Title, "Patch notes"),
                new NoteLine(NoteKind.Paragraph, "Newest first, wrapped."),
                new NoteLine(NoteKind.Heading, "v0.1.1 (2026-11-27)"),
                new NoteLine(NoteKind.Subheading, "Added"),
                new NoteLine(NoteKind.Bullet, "Updates. Run niskala update. It verifies SHA256SUMS.txt.", 0, "•"),
                new NoteLine(NoteKind.Bullet, "nested item", 1, "•"),
                new NoteLine(NoteKind.Bullet, "First", 0, "1."),
                new NoteLine(NoteKind.Bullet, "Second", 0, "2."),
            ],
            lines);
    }

    [Fact]
    public void Tables_become_labelled_rows()
    {
        IReadOnlyList<NoteLine> lines = PatchNotes.Parse(
            "| Package | v0.1.0 | v0.1.1 |\n|---|:---:|---:|\n| Desktop app zip | 55.5 MB | 29.1 MB |\n| `a \\| b` | | 1 |\n");

        Assert.Equal(
            [
                new NoteLine(NoteKind.Bullet, "Desktop app zip: v0.1.0 55.5 MB, v0.1.1 29.1 MB", 0, "•"),
                new NoteLine(NoteKind.Bullet, "a | b: v0.1.1 1", 0, "•"),
            ],
            lines);
    }

    [Theory]
    [InlineData("[the release page](https://github.com/x/y)", "the release page")]
    [InlineData("![logo](logo.png) text", "logo text")]
    [InlineData("<https://github.com/x/y>", "https://github.com/x/y")]
    [InlineData("~~old~~ new", "old new")]
    [InlineData("__strong__ and *em*", "strong and em")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData(@"lib\PATCHNOTES.md and \*escaped\*", @"lib\PATCHNOTES.md and *escaped*")]
    [InlineData("``code with ` tick``", "code with ` tick")]
    [InlineData("`**kept**`", "**kept**")]
    [InlineData("line<br>break", "linebreak")]
    public void Inline_markup_is_removed(string markdown, string text)
    {
        Assert.Equal(text, PatchNotes.Plain(markdown));
    }

    [Fact]
    public void Fences_comments_and_rules_are_not_shown_as_markup()
    {
        IReadOnlyList<NoteLine> lines = PatchNotes.Parse("<!-- hidden\nstill hidden -->\n---\n```powershell\nniskala update check\n```\n> quoted **text**\n");

        Assert.Equal(
            [
                new NoteLine(NoteKind.Paragraph, "niskala update check"),
                new NoteLine(NoteKind.Paragraph, "quoted text"),
            ],
            lines);
    }

    [Fact]
    public void A_release_body_from_github_reads_cleanly()
    {
        IReadOnlyList<NoteLine> lines = PatchNotes.Parse(
            "## What's Changed\r\n* Signed builds by a contributor in https://github.com/Adriansyah101/NiskalaUtilities/pull/2\r\n\r\n**Full Changelog**: https://github.com/Adriansyah101/NiskalaUtilities/compare/v0.1.0...v0.1.1");

        Assert.Equal(NoteKind.Heading, lines[0].Kind);
        Assert.Equal("Signed builds by a contributor in https://github.com/Adriansyah101/NiskalaUtilities/pull/2", lines[1].Text);
        Assert.Equal("Full Changelog: https://github.com/Adriansyah101/NiskalaUtilities/compare/v0.1.0...v0.1.1", lines[2].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void Nothing_to_show_is_an_empty_list(string? markdown)
    {
        Assert.Empty(PatchNotes.Parse(markdown));
    }

    [Fact]
    public void The_embedded_notes_are_the_repository_file()
    {
        string file = File.ReadAllText(Path.Combine(TestRoots.Repository(), PatchNotes.FileName));

        Assert.Equal(file, PatchNotes.Embedded());
        Assert.Equal(file, PatchNotes.Bundled());
    }

    [Fact]
    public void The_repository_notes_cover_this_version_and_render_without_markup()
    {
        string repository = TestRoots.Repository();
        string version = XDocument.Load(Path.Combine(repository, "Directory.Build.props")).Descendants("Version").Single().Value;
        string file = File.ReadAllText(Path.Combine(repository, PatchNotes.FileName));
        IReadOnlyList<NoteLine> lines = PatchNotes.Parse(file);

        Assert.Equal(NoteKind.Title, lines[0].Kind);
        Assert.StartsWith($"v{version} ", lines.First(l => l.Kind == NoteKind.Heading).Text, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Kind == NoteKind.Heading && l.Text.StartsWith("v0.1.0 ", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain(RawSyntax, s => l.Text.Contains(s, StringComparison.Ordinal)));
        Assert.DoesNotContain("\n", file.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_next_to_the_program_is_read_when_present()
    {
        string folder = TestRoots.NewFolder();
        string copy = Path.Combine(folder, PatchNotes.FileName);
        File.WriteAllText(copy, "# Copy");

        Assert.Equal("# Copy", PatchNotes.ReadFile(copy));
        Assert.Null(PatchNotes.ReadFile(Path.Combine(folder, "missing.md")));
        Assert.NotNull(PatchNotes.Bundled(folder));
    }
}
