using Niskala.UI.Services;
using Niskala.UI.ViewModels;
using Niskala.Updater;

namespace Niskala.UI.Tests;

public sealed class UpdatePromptTests
{
    private const string Notes =
        "## v0.1.3 (2026-11-28)\r\n\r\n### Added\r\n\r\n- **Update prompt.** A dialog offers the new version.\r\n- **Sidebar motion.** Categories open smoothly.\r\n  - nested detail\r\n\r\n### Fixed\r\n\r\n- One.\r\n- Two.\r\n- Three.\r\n";

    [Fact]
    public void The_check_at_start_offers_a_newer_release_once()
    {
        var prompt = new UpdatePrompt();

        Assert.True(prompt.Offer(Check(), automatic: true, packageReady: true));
        UpdateOffer offer = Assert.IsType<UpdateOffer>(prompt.Pending);
        Assert.Equal("0.1.2", offer.Current);
        Assert.Equal("0.1.3", offer.Latest);
        Assert.False(prompt.Offer(Check(), automatic: true, packageReady: true));
    }

    [Fact]
    public void A_check_started_by_hand_never_opens_the_dialog()
    {
        var prompt = new UpdatePrompt();

        Assert.False(prompt.Offer(Check(), automatic: false, packageReady: true));
        Assert.Null(prompt.Pending);
    }

    [Theory]
    [InlineData(UpdateStatus.UpToDate, true)]
    [InlineData(UpdateStatus.NoRelease, true)]
    [InlineData(UpdateStatus.Available, false)]
    public void Nothing_is_offered_without_a_newer_release_that_can_be_installed(UpdateStatus status, bool packageReady)
    {
        var prompt = new UpdatePrompt();

        Assert.False(prompt.Offer(Check(status), automatic: true, packageReady));
        Assert.Null(prompt.Pending);
    }

    [Fact]
    public void After_Later_the_dialog_stays_away_for_the_rest_of_the_session()
    {
        var prompt = new UpdatePrompt();
        prompt.Offer(Check(), automatic: true, packageReady: true);

        UpdateOffer? shown = prompt.Take();
        prompt.Close(UpdatePromptChoice.Later);

        Assert.NotNull(shown);
        Assert.True(prompt.HasShown);
        Assert.Equal(UpdatePromptChoice.Later, prompt.Choice);
        Assert.Null(prompt.Take());
        Assert.False(prompt.Offer(Check(), automatic: true, packageReady: true));
        Assert.False(prompt.Offer(Check(latest: "0.1.4"), automatic: true, packageReady: true));
        Assert.Null(prompt.Pending);
    }

    [Fact]
    public void An_offer_waiting_for_the_window_is_shown_only_once()
    {
        var prompt = new UpdatePrompt();
        prompt.Offer(Check(), automatic: true, packageReady: true);

        Assert.NotNull(prompt.Take());
        Assert.Null(prompt.Take());
    }

    [Theory]
    [InlineData(true, false, 0, false, true)]
    [InlineData(false, false, 0, false, false)]
    [InlineData(true, true, 0, false, false)]
    [InlineData(true, false, 1, false, false)]
    [InlineData(true, false, 2, false, false)]
    [InlineData(true, false, 0, true, false)]
    public void The_dialog_waits_for_a_ready_window_with_no_other_dialog_or_running_task(bool ready, bool hidden, int popups, bool busy, bool expected)
    {
        Assert.Equal(expected, UpdatePrompt.CanShow(ready, hidden, popups, busy));
    }

    [Fact]
    public void The_summary_lists_the_first_top_level_items_without_markup()
    {
        (TextList highlights, int more) = UpdateOffer.Summarize(Notes);

        Assert.Equal(["Update prompt. A dialog offers the new version.", "Sidebar motion. Categories open smoothly.", "One.", "Two."], highlights);
        Assert.Equal(1, more);
    }

    [Fact]
    public void Long_items_are_cut_at_a_word_with_an_ellipsis()
    {
        string notes = "- " + string.Join(' ', Enumerable.Repeat("verified", 40));

        string line = Assert.Single(UpdateOffer.Summarize(notes).Highlights);

        Assert.True(line.Length <= UpdateOffer.MaxLength);
        Assert.EndsWith("verified…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_without_items_fall_back_to_their_paragraphs()
    {
        (TextList highlights, int more) = UpdateOffer.Summarize("# Release\r\n\r\nSmaller and faster.\r\n");

        Assert.Equal(["Smaller and faster."], highlights);
        Assert.Equal(0, more);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_release_without_notes_still_makes_an_offer(string? notes)
    {
        var prompt = new UpdatePrompt();

        Assert.True(prompt.Offer(Check(notes: notes), automatic: true, packageReady: true));
        Assert.False(prompt.Pending!.HasHighlights);
        Assert.False(prompt.Pending.HasMore);
    }

    [Theory]
    [InlineData(false, "Niskala Utilities 0.1.3 is available", "Update now", "Review first", "Later", "and 3 more changes on the Updates page")]
    [InlineData(true, "Niskala Utilities 0.1.3 tersedia", "Perbarui sekarang", "Tinjau dulu", "Nanti", "dan 3 perubahan lain di halaman Pembaruan")]
    public void The_dialog_speaks_both_languages(bool indonesian, string title, string now, string review, string later, string more)
    {
        Strings s = Strings.For(indonesian);

        Assert.Equal(title, s.UpdatePromptTitle("0.1.3"));
        Assert.Equal(now, s.UpdateNow);
        Assert.Equal(review, s.UpdateReview);
        Assert.Equal(later, s.UpdateLater);
        Assert.Equal(more, s.UpdatePromptMore(3));
        Assert.False(string.IsNullOrWhiteSpace(s.UpdatePromptIntro));
        Assert.Contains("SHA256SUMS.txt", s.UpdatePromptIntro, StringComparison.Ordinal);
    }

    private static UpdateCheck Check(UpdateStatus status = UpdateStatus.Available, string latest = "0.1.3", string? notes = Notes) =>
        new(status, ReleaseVersion.Parse("0.1.2"), ReleaseVersion.Parse(latest), $"V{latest}", notes,
            new Uri($"https://github.com/Adriansyah101/NiskalaUtilities/releases/tag/V{latest}"), DateTimeOffset.UnixEpoch, ReleaseAssets.None);
}
