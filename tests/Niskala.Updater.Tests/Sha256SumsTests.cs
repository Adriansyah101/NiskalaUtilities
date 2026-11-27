namespace Niskala.Updater.Tests;

public sealed class Sha256SumsTests
{
    private const string Setup = "a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3";
    private const string Cli = "180FF81944CB3ED9E97B515E8C0F9E0824F513636E5D52F4CFB05CF52B965493";

    [Fact]
    public void The_release_script_format_parses()
    {
        IReadOnlyDictionary<string, string> sums = Sha256Sums.Parse(
            $"{Setup}  NiskalaUtilities-Setup-v0.1.1-win-x64.exe\n{Cli}  NiskalaUtilities-CLI-v0.1.1-win-x64.zip\n");

        Assert.Equal(2, sums.Count);
        Assert.Equal(Setup, sums["NiskalaUtilities-Setup-v0.1.1-win-x64.exe"]);
        Assert.Equal(Cli.ToLowerInvariant(), sums["NiskalaUtilities-CLI-v0.1.1-win-x64.zip"]);
    }

    [Fact]
    public void Crlf_blank_lines_and_binary_markers_are_accepted()
    {
        IReadOnlyDictionary<string, string> sums = Sha256Sums.Parse($"\r\n{Setup} *a.zip\r\n\r\n{Cli} b.zip\r\n");
        Assert.Equal(Setup, sums["a.zip"]);
        Assert.Equal(Cli.ToLowerInvariant(), sums["b.zip"]);
        Assert.Empty(Sha256Sums.Parse(string.Empty));
    }

    [Theory]
    [InlineData("a0276e6c  a.zip")]
    [InlineData("z0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3  a.zip")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3a  a.zip")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3\ta.zip")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3  ")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3  ..\\a.zip")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3  lib/a.zip")]
    [InlineData("a0276e6c8098da03f4ffd028e6a4b6156482b1f621fef95f8aa7d231fe7045f3  a.zip ")]
    public void Malformed_lines_are_refused(string line)
    {
        Assert.Throws<FormatException>(() => Sha256Sums.Parse(line));
    }

    [Fact]
    public void A_file_listed_twice_is_refused()
    {
        Assert.Throws<FormatException>(() => Sha256Sums.Parse($"{Setup}  a.zip\n{Cli}  a.zip\n"));
    }
}
