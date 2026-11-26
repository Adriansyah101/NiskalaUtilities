using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Niskala.Core.Models;
using Niskala.Core.Safety;

namespace Niskala.Scanner.Leftovers;

public enum TokenOrigin
{
    Name,
    NameWithoutPublisher,
    InstallFolder,
}

public sealed record NameToken(string Text, string Compact, TokenOrigin Origin)
{
    public bool IsShort => Compact.Length < TokenNormalizer.MinimumLength;
}

public sealed record AppTokens(IReadOnlyList<NameToken> Product, string? Publisher);

public enum NameMatchKind
{
    None,
    Exact,
    Partial,
}

public readonly record struct NameMatch(NameMatchKind Kind, NameToken? Token);

public static partial class TokenNormalizer
{
    public const int MinimumLength = 4;

    private static readonly FrozenSet<string> CommonWords = FrozenSet.ToFrozenSet(
    [
        "microsoft", "windows", "common files", "update", "setup", "software", "tools", "system", "data",
        "app", "cache", "temp", "shared", "runtime", "packages",
    ]);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string value = Marks().Replace(text, " ").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        value = Version().Replace(value, " ");
        value = Architecture().Replace(value, " ");
        value = Locale().Replace(value, " ");
        value = Separators().Replace(value, " ").Trim();
        while (CompanySuffix().Match(value) is { Success: true } suffix)
        {
            value = value[..suffix.Index].TrimEnd();
        }

        return value;
    }

    public static string Compact(string normalized) => normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

    public static bool IsCommon(string normalized) =>
        normalized.Length == 0 || CommonWords.Contains(normalized) || normalized.Split(' ').All(CommonWords.Contains);

    public static AppTokens For(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        string name = Normalize(app.DisplayName);
        string publisher = Normalize(app.Publisher);
        var tokens = new List<NameToken>();

        Add(name, TokenOrigin.Name);
        if (publisher.Length > 0 && name.StartsWith(publisher + " ", StringComparison.Ordinal))
        {
            Add(name[(publisher.Length + 1)..], TokenOrigin.NameWithoutPublisher);
        }

        if (app.InstallLocationGuard == PathVerdict.Allowed && app.InstallLocation is { } location)
        {
            Add(Normalize(Path.GetFileName(location.TrimEnd('\\', '/'))), TokenOrigin.InstallFolder);
        }

        return new AppTokens(tokens, IsCommon(publisher) ? null : Compact(publisher));

        void Add(string text, TokenOrigin origin)
        {
            string compact = Compact(text);
            if (!IsCommon(text) && tokens.TrueForAll(t => t.Compact != compact))
            {
                tokens.Add(new NameToken(text, compact, origin));
            }
        }
    }

    public static NameMatch MatchProduct(string name, AppTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        string normalized = Normalize(name);
        string segment = Compact(normalized);
        if (segment.Length == 0)
        {
            return default;
        }

        foreach (NameToken token in tokens.Product)
        {
            if (token.Compact == segment)
            {
                return new NameMatch(NameMatchKind.Exact, token);
            }
        }

        if (segment.Length < MinimumLength || IsCommon(normalized))
        {
            return default;
        }

        foreach (NameToken token in tokens.Product)
        {
            if (!token.IsShort && (segment.Contains(token.Compact, StringComparison.Ordinal) || token.Compact.Contains(segment, StringComparison.Ordinal)))
            {
                return new NameMatch(NameMatchKind.Partial, token);
            }
        }

        return default;
    }

    public static bool MatchesPublisher(string name, AppTokens tokens) =>
        tokens.Publisher is { } publisher && Compact(Normalize(name)) == publisher;

    [GeneratedRegex("[\u2122\u00AE\u00A9]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Version();

    [GeneratedRegex(@"\b(x64|x86|x86_64|amd64|arm64|64-bit|32-bit)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"\b[a-z]{2}-[a-z]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Locale();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"(^|\s)(inc|ltd|llc|gmbh|corporation|corp|co|limited|s r o|ag|b v|sa|srl|pty)$", RegexOptions.CultureInvariant)]
    private static partial Regex CompanySuffix();
}
