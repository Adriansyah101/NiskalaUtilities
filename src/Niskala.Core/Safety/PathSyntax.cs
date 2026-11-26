using System.Buffers;

namespace Niskala.Core.Safety;

public static class PathSyntax
{
    public const int MaxPathLength = 32_767;

    private static readonly SearchValues<char> WildcardAndQuoteChars = SearchValues.Create("*?\"<>|");

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    public static PathVerdict TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return PathVerdict.Empty;
        }

        if (input.Length > MaxPathLength)
        {
            return PathVerdict.TooLong;
        }

        foreach (char c in input)
        {
            if (c < 0x20)
            {
                return PathVerdict.InvalidCharacters;
            }
        }

        string path = input.Replace('/', '\\');

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            string rest = path[4..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return PathVerdict.UncPath;
            }

            if (!IsDriveRooted(rest))
            {
                return PathVerdict.DevicePath;
            }

            path = rest;
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return PathVerdict.DevicePath;
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathVerdict.UncPath;
        }

        if (!IsDriveRooted(path))
        {
            return PathVerdict.NotFullyQualified;
        }

        string[] segments = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            PathVerdict verdict = CheckSegment(segment);
            if (verdict != PathVerdict.Allowed)
            {
                return verdict;
            }
        }

        normalized = string.Concat(char.ToUpperInvariant(path[0]).ToString(), @":\", string.Join('\\', segments));
        return normalized.Length > MaxPathLength ? PathVerdict.TooLong : PathVerdict.Allowed;
    }

    public static bool IsSameOrUnder(string path, string root, out bool isSame)
    {
        isSame = string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        if (isSame)
        {
            return true;
        }

        if (root.EndsWith('\\'))
        {
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        return path.Length > root.Length
            && path[root.Length] == '\\'
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriveRoot(string normalized) => normalized.Length == 3;

    public static string[] GetSegments(string normalized) =>
        normalized.Length <= 3 ? [] : normalized[3..].Split('\\');

    private static PathVerdict CheckSegment(string segment)
    {
        if (segment is "." or "..")
        {
            return PathVerdict.TraversalSegment;
        }

        if (segment.Contains(':'))
        {
            return PathVerdict.AlternateDataStream;
        }

        if (segment.AsSpan().ContainsAny(WildcardAndQuoteChars))
        {
            return PathVerdict.InvalidCharacters;
        }

        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return PathVerdict.TrailingDotOrSpace;
        }

        int dot = segment.IndexOf('.');
        string baseName = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return ReservedDeviceNames.Contains(baseName) ? PathVerdict.ReservedDeviceName : PathVerdict.Allowed;
    }

    private static bool IsDriveRooted(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
}
