using Niskala.Scanner.Leftovers;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class ConfidenceScorerTests
{
    [Theory]
    [InlineData(100, ConfidenceTier.Safe)]
    [InlineData(80, ConfidenceTier.Safe)]
    [InlineData(79, ConfidenceTier.Review)]
    [InlineData(50, ConfidenceTier.Review)]
    [InlineData(49, ConfidenceTier.Low)]
    [InlineData(0, ConfidenceTier.Low)]
    public void Tiers_follow_the_thresholds(int score, ConfidenceTier tier) => Assert.Equal(tier, ConfidenceScorer.TierOf(score));

    [Fact]
    public void Sums_each_signal_once_and_keeps_every_reason()
    {
        var scorer = new ConfidenceScorer(null, []);
        LeftoverItem item = scorer.Score(Candidate(
            "Photo Editor",
            (LeftoverSignal.ExactName, "name"),
            (LeftoverSignal.PublisherAndProduct, @"Acme\Photo Editor"),
            (LeftoverSignal.ExactName, "same name again")));

        Assert.Equal(65, item.Score);
        Assert.Equal(ConfidenceTier.Review, item.Tier);
        Assert.Equal([LeftoverSignal.ExactName, LeftoverSignal.PublisherAndProduct], item.Reasons.Select(r => r.Signal));
        Assert.Equal("name", item.Reasons[0].Detail);
    }

    [Fact]
    public void Clamps_to_the_0_100_range()
    {
        var scorer = new ConfidenceScorer(null, []);

        Assert.Equal(100, scorer.Score(Candidate(null, (LeftoverSignal.InstallerIdentity, "a"), (LeftoverSignal.InFingerprint, "b"))).Score);
        Assert.Equal(0, scorer.Score(Candidate("Acme Tools", (LeftoverSignal.PartialName, "c"))).Score);
    }

    [Fact]
    public void A_partial_name_does_not_count_against_owned_items()
    {
        var scorer = new ConfidenceScorer(null, []);

        LeftoverItem item = scorer.Score(Candidate("Firefox Private Browsing", (LeftoverSignal.PartialName, "partial"), (LeftoverSignal.InFingerprint, "shortcut")));

        Assert.Equal(60, item.Score);
        Assert.DoesNotContain(item.Reasons, r => r.Signal == LeftoverSignal.PartialName);
    }

    [Fact]
    public void Names_of_other_installed_apps_lower_the_score()
    {
        AppTokens other = TokenNormalizer.For(App("reg:hklm64:Studio", "Acme Studio") with { Publisher = "Acme" });
        var scorer = new ConfidenceScorer(null, [new OtherApp("Acme Studio", other)]);

        LeftoverItem shared = scorer.Score(Candidate("Acme", (LeftoverSignal.ExactName, "name")));
        LeftoverItem own = scorer.Score(Candidate("Photo Editor", (LeftoverSignal.ExactName, "name")));

        Assert.Equal(0, shared.Score);
        Assert.Contains(shared.Reasons, r => r.Signal == LeftoverSignal.OtherInstalledApp && r.Detail.Contains("Acme Studio", StringComparison.Ordinal));
        Assert.Equal(40, own.Score);
    }

    [Fact]
    public void Creation_within_a_day_of_the_install_date_adds_ten()
    {
        var scorer = new ConfidenceScorer(new DateOnly(2026, 3, 10), []);
        DateTime near = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        DateTime far = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.Equal(50, scorer.Score(Candidate("Photo Editor", (LeftoverSignal.ExactName, "name")) with { Created = near }).Score);
        Assert.Equal(40, scorer.Score(Candidate("Photo Editor", (LeftoverSignal.ExactName, "name")) with { Created = far }).Score);
    }

    [Fact]
    public void Personal_data_found_by_name_or_better_is_listed_for_review()
    {
        var scorer = new ConfidenceScorer(null, []);
        const string saves = "may hold saves";

        LeftoverItem byName = scorer.Score(Candidate("Garry's Mod", (LeftoverSignal.ExactName, "name")) with { PersonalData = saves });
        LeftoverItem owned = scorer.Score(Candidate(null, (LeftoverSignal.SteamAppId, "userdata"), (LeftoverSignal.InFingerprint, "b")) with { PersonalData = saves });
        LeftoverItem partial = scorer.Score(Candidate("Garry's Mod Tools", (LeftoverSignal.PartialName, "c")) with { PersonalData = saves });

        Assert.Equal((50, ConfidenceTier.Review), (byName.Score, byName.Tier));
        Assert.Equal(new LeftoverReason(LeftoverSignal.PersonalData, 10, saves), byName.Reasons[^1]);
        Assert.Equal((79, ConfidenceTier.Review), (owned.Score, owned.Tier));
        Assert.Equal(-71, owned.Reasons[^1].Weight);
        Assert.Equal(ConfidenceTier.Low, partial.Tier);
        Assert.DoesNotContain(partial.Reasons, r => r.Signal == LeftoverSignal.PersonalData);
    }

    private static LeftoverCandidate Candidate(string? name, params (LeftoverSignal, string)[] evidence) =>
        new(LeftoverKind.Folder, $@"C:\Data\{name ?? "x"}", null, name, null, evidence);
}
