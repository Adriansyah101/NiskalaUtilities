using System.Globalization;
using System.Text.RegularExpressions;

namespace Niskala.Repair.Output;

public static partial class RepairProgressParser
{
    private const double Complete = 100;

    public static double? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double? percent = null;
        foreach (ValueMatch match in Percent().EnumerateMatches(text))
        {
            string number = text.AsSpan(match.Index, match.Length).TrimEnd('%').TrimEnd().ToString().Replace(',', '.');
            if (double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && value <= Complete)
            {
                percent = value;
            }
        }

        return percent;
    }

    [GeneratedRegex(@"(?<![\d.,])\d{1,3}(?:[.,]\d{1,2})?\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex Percent();
}
