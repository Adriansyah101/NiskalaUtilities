using System.Globalization;
using System.Text.RegularExpressions;

namespace Niskala.Repair.Output;

public readonly record struct PercentScan(double? Percent, int Count);

public static partial class RepairProgressParser
{
    public const double Complete = 100;

    public static double? TryParse(string text) => Scan(text).Percent;

    public static PercentScan Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double? percent = null;
        int count = 0;
        foreach (ValueMatch match in Percent().EnumerateMatches(text))
        {
            string number = text.AsSpan(match.Index, match.Length).TrimEnd('%').TrimEnd().ToString().Replace(',', '.');
            if (double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && value <= Complete)
            {
                percent = value;
                count++;
            }
        }

        return new PercentScan(percent, count);
    }

    [GeneratedRegex(@"(?<![\d.,])\d{1,3}(?:[.,]\d{1,2})?\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex Percent();
}

public sealed class ProgressReader
{
    private int _count;

    public double? Read(ConsoleLine line)
    {
        PercentScan scan = RepairProgressParser.Scan(line.Text);
        if (scan.Percent is null)
        {
            return null;
        }

        if (line.IsOpen)
        {
            return _count > 0 && scan.Count >= _count ? scan.Percent : null;
        }

        _count = scan.Count;
        return scan.Percent;
    }
}
