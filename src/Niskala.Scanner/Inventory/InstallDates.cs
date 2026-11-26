using System.Globalization;

namespace Niskala.Scanner.Inventory;

internal static class InstallDates
{
    private static readonly string[] Formats =
    [
        "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy", "dd.MM.yyyy",
    ];

    public static DateOnly? Parse(string? text, DateOnly today) =>
        text is not null
        && DateOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
        && date.Year >= 1995
        && date <= today.AddDays(1)
            ? date
            : null;
}
