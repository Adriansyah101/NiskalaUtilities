using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Niskala.Updater;

public sealed record ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly SearchValues<char> IdentifierCharacters =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-");

    private ReleaseVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out ReleaseVersion? version) ? version : throw new FormatException($"'{text}' is not a release version.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        int build = span.IndexOf('+');
        if (build >= 0)
        {
            if (!AreIdentifiers(span[(build + 1)..], numericRules: false))
            {
                return false;
            }

            span = span[..build];
        }

        ReadOnlySpan<char> prerelease = [];
        int dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];
            if (!AreIdentifiers(prerelease, numericRules: true))
            {
                return false;
            }
        }

        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3
            || !TryNumber(span[parts[0]], out int major)
            || !TryNumber(span[parts[1]], out int minor)
            || !TryNumber(span[parts[2]], out int patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease.ToString());
        return true;
    }

    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) < 0;

    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) > 0;

    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) <= 0;

    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) >= 0;

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0 || Prerelease == other.Prerelease)
        {
            return core;
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        string[] mine = Prerelease.Split('.');
        string[] theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            int order = CompareIdentifier(mine[i], theirs[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => IsPrerelease
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = IsDigits(left);
        bool rightNumeric = IsDigits(right);
        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static bool TryNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        return IsDigits(text) && (text.Length == 1 || text[0] != '0')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool AreIdentifiers(ReadOnlySpan<char> text, bool numericRules)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (Range range in text.Split('.'))
        {
            ReadOnlySpan<char> identifier = text[range];
            if (identifier.IsEmpty || identifier.ContainsAnyExcept(IdentifierCharacters))
            {
                return false;
            }

            if (numericRules && identifier.Length > 1 && identifier[0] == '0' && IsDigits(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> text) => !text.IsEmpty && !text.ContainsAnyExceptInRange('0', '9');
}
