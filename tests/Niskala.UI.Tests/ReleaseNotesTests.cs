using Niskala.UI.ViewModels;
using Niskala.Updater;

namespace Niskala.UI.Tests;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void The_desktop_app_carries_its_patch_notes()
    {
        IReadOnlyList<NoteLine> notes = PatchNotes.Parse(PatchNotes.Bundled());

        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.3.2")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.3.1")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.3.0")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.2.0")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.1.3")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.1.2")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.1.1")));
        Assert.True(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.1.0")));
        Assert.False(UpdatesViewModel.Covers(notes, ReleaseVersion.Parse("0.3.3")));
    }

    [Fact]
    public void Rows_are_styled_by_kind_and_indented_by_level()
    {
        var title = new NoteRow(new NoteLine(NoteKind.Title, "Patch notes"));
        var heading = new NoteRow(new NoteLine(NoteKind.Heading, "v0.1.1"));
        var nested = new NoteRow(new NoteLine(NoteKind.Bullet, "nested", 2, "•"));
        var paragraph = new NoteRow(new NoteLine(NoteKind.Paragraph, "text"));

        Assert.True(title.Size > heading.Size && heading.Size > paragraph.Size);
        Assert.Equal(0, title.Spacing.Top);
        Assert.True(heading.Weight.Weight > paragraph.Weight.Weight);
        Assert.Equal(36, nested.Spacing.Left);
        Assert.True(nested.HasMarker);
        Assert.False(paragraph.HasMarker);
    }
}
