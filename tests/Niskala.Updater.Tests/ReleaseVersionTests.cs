namespace Niskala.Updater.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.1", 0, 1, 1, "")]
    [InlineData("v0.1.1", 0, 1, 1, "")]
    [InlineData("V0.1.0", 0, 1, 0, "")]
    [InlineData(" v1.20.300 ", 1, 20, 300, "")]
    [InlineData("1.0.0-beta.1", 1, 0, 0, "beta.1")]
    [InlineData("1.0.0-rc-1+build.7", 1, 0, 0, "rc-1")]
    [InlineData("2.0.0+sha.5114f85", 2, 0, 0, "")]
    public void Valid_versions_parse(string text, int major, int minor, int patch, string prerelease)
    {
        Assert.True(ReleaseVersion.TryParse(text, out ReleaseVersion? version));
        Assert.Equal((major, minor, patch, prerelease), (version.Major, version.Minor, version.Patch, version.Prerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-be_ta")]
    [InlineData("1.0.0+")]
    [InlineData("1.-1.0")]
    [InlineData("latest")]
    [InlineData("99999999999.0.0")]
    public void Invalid_versions_are_refused(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void Precedence_follows_semantic_versioning()
    {
        string[] ordered =
        [
            "0.9.9", "0.10.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0",
        ];

        for (int i = 1; i < ordered.Length; i++)
        {
            ReleaseVersion lower = ReleaseVersion.Parse(ordered[i - 1]);
            ReleaseVersion higher = ReleaseVersion.Parse(ordered[i]);
            Assert.True(lower < higher, $"{lower} < {higher}");
            Assert.True(higher > lower, $"{higher} > {lower}");
            Assert.True(lower.CompareTo(higher) < 0);
        }
    }

    [Fact]
    public void Build_metadata_and_the_v_prefix_do_not_change_identity()
    {
        ReleaseVersion plain = ReleaseVersion.Parse("0.1.1");
        ReleaseVersion tagged = ReleaseVersion.Parse("v0.1.1+abc");
        Assert.Equal(plain, tagged);
        Assert.Equal(0, plain.CompareTo(tagged));
        Assert.True(plain >= tagged && plain <= tagged);
        Assert.Equal("0.1.1", tagged.ToString());
        Assert.Equal("1.0.0-rc.1", ReleaseVersion.Parse("V1.0.0-rc.1+7").ToString());
    }

    [Fact]
    public void The_running_version_comes_from_the_build()
    {
        Assert.False(UpdateService.CurrentVersion.IsPrerelease);
        Assert.True(UpdateService.CurrentVersion >= Releases.V011);
    }
}
