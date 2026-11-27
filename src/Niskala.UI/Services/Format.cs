using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Niskala.UI.Services;

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static CultureInfo Culture => Strings.Current.Culture;

    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(Culture, $"{bytes} {Units[0]}")
            : string.Create(Culture, $"{value:0.#} {Units[unit]}");
    }

    public static string Date(DateOnly? date) => date is { } d ? d.ToString("d MMM yyyy", Culture) : string.Empty;

    public static string When(DateTimeOffset time) => time.ToLocalTime().ToString("d MMM yyyy HH:mm", Culture);

    public static string Number(long value) => value.ToString("N0", Culture);

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NoText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Empty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    public static SolidColorBrush? Plate(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        string text = color.Trim();
        if (text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return text.Length switch
            {
                7 => new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)),
                9 when value >> 24 != 0 => new SolidColorBrush(ColorHelper.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)),
                _ => null,
            };
        }

        System.Drawing.Color named = System.Drawing.Color.FromName(text);
        return named.IsKnownColor && named.A != 0 ? new SolidColorBrush(ColorHelper.FromArgb(named.A, named.R, named.G, named.B)) : null;
    }
}
