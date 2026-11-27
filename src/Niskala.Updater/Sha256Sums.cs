namespace Niskala.Updater;

public static class Sha256Sums
{
    public const int HashLength = 64;

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        int number = 0;
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length < HashLength + 2 || !IsHash(line.AsSpan(0, HashLength)) || line[HashLength] != ' ')
            {
                throw new FormatException($"Line {number} of {ReleaseNames.Checksums} is not '<sha256>  <file>'.");
            }

            string name = line[(HashLength + 1)..];
            if (name.Length > 0 && (name[0] == ' ' || name[0] == '*'))
            {
                name = name[1..];
            }

            if (name.Length == 0 || name.Trim() != name || Path.GetFileName(name) != name)
            {
                throw new FormatException($"Line {number} of {ReleaseNames.Checksums} names no plain file.");
            }

            if (!sums.TryAdd(name, line[..HashLength].ToLowerInvariant()))
            {
                throw new FormatException($"{ReleaseNames.Checksums} lists {name} twice.");
            }
        }

        return sums;
    }

    public static bool IsHash(ReadOnlySpan<char> text)
    {
        if (text.Length != HashLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
