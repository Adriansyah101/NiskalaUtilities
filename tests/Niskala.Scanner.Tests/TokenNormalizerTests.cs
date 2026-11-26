using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Leftovers;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class TokenNormalizerTests
{
    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "mozilla firefox")]
    [InlineData("7-Zip 23.01 (x64)", "7 zip")]
    [InlineData("Acme\u2122 Photo Editor\u00AE v2.1.0", "acme photo editor")]
    [InlineData("Python 3.12.1 (64-bit)", "python")]
    [InlineData("Acme Software, Inc.", "acme software")]
    [InlineData("Contoso Pty Ltd", "contoso")]
    [InlineData("Fabrikam, LLC", "fabrikam")]
    [InlineData("Northwind Corp.", "northwind")]
    [InlineData("Litware Corporation", "litware")]
    [InlineData("Tailspin Toys GmbH", "tailspin toys")]
    [InlineData("Wingtip Ltd.", "wingtip")]
    [InlineData("\uFF21\uFF43\uFF4D\uFF45", "acme")]
    [InlineData("Notepad++", "notepad")]
    [InlineData("   ", "")]
    public void Normalizes_names(string input, string expected) => Assert.Equal(expected, TokenNormalizer.Normalize(input));

    [Fact]
    public void Builds_name_install_folder_and_publisher_tokens()
    {
        InstalledApp app = App("reg:hklm64:Firefox", "Mozilla Firefox (x64 en-US)") with
        {
            Publisher = "Mozilla Corporation",
            InstallLocation = @"C:\Program Files\Firefox Main\",
            InstallLocationGuard = PathVerdict.Allowed,
        };

        AppTokens tokens = TokenNormalizer.For(app);

        Assert.Equal(["mozillafirefox", "firefox", "firefoxmain"], tokens.Product.Select(t => t.Compact));
        Assert.Equal([TokenOrigin.Name, TokenOrigin.NameWithoutPublisher, TokenOrigin.InstallFolder], tokens.Product.Select(t => t.Origin));
        Assert.Equal("mozilla", tokens.Publisher);
    }

    [Fact]
    public void Common_words_and_unchecked_folders_give_no_tokens()
    {
        InstalledApp app = App("reg:hklm64:Update", "Windows Update Tools") with
        {
            Publisher = "Microsoft Corporation",
            InstallLocation = @"C:\Program Files",
            InstallLocationGuard = PathVerdict.ProtectedSelf,
        };

        AppTokens tokens = TokenNormalizer.For(app);

        Assert.Empty(tokens.Product);
        Assert.Null(tokens.Publisher);
    }

    [Theory]
    [InlineData("AcmePhotoEditor", NameMatchKind.Exact)]
    [InlineData("acme-photo-editor", NameMatchKind.Exact)]
    [InlineData("Acme Photo Editor 2", NameMatchKind.Partial)]
    [InlineData("PhotoEd", NameMatchKind.Partial)]
    [InlineData("Cache", NameMatchKind.None)]
    [InlineData("Acm", NameMatchKind.None)]
    [InlineData("Contoso", NameMatchKind.None)]
    public void Matches_names_exactly_or_partially(string name, NameMatchKind expected)
    {
        AppTokens tokens = TokenNormalizer.For(App("reg:hklm64:Acme", "Acme Photo Editor") with { Publisher = "Acme" });

        Assert.Equal(expected, TokenNormalizer.MatchProduct(name, tokens).Kind);
    }

    [Fact]
    public void Short_tokens_match_exactly_but_never_partially()
    {
        AppTokens tokens = TokenNormalizer.For(App("reg:hklm64:Git", "Git") with { Publisher = "The Git Development Community" });

        NameMatch exact = TokenNormalizer.MatchProduct("GIT", tokens);
        Assert.Equal(NameMatchKind.Exact, exact.Kind);
        Assert.True(exact.Token!.IsShort);
        Assert.Equal(NameMatchKind.None, TokenNormalizer.MatchProduct("GitHub Desktop", tokens).Kind);
        Assert.True(TokenNormalizer.MatchesPublisher("The Git Development Community", tokens));
    }
}
