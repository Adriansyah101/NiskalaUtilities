namespace Niskala.UI.Services;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public readonly record struct TitleBarFit(double LauncherWidth, bool ShowVersion, bool ShowAdmin);

public static class WindowPlacement
{
    public const double PreferredWidth = 1320;
    public const double PreferredHeight = 860;
    public const double MinimumWidth = 960;
    public const double MinimumHeight = 640;
    public const double WorkAreaShare = 0.92;

    public const double BrandWidth = 200;
    public const double VersionWidth = 56;
    public const double AdminWidth = 118;
    public const double TitleBarMargin = 40;
    public const double LauncherMinWidth = 160;
    public const double LauncherMaxWidth = 460;
    public const double VersionFrom = 900;
    public const double AdminFrom = 760;

    public static (int Width, int Height) Minimum(PixelRect work, double scale)
    {
        double factor = scale > 0 ? scale : 1;
        return (
            Math.Max(1, Math.Min((int)Math.Round(MinimumWidth * factor), work.Width)),
            Math.Max(1, Math.Min((int)Math.Round(MinimumHeight * factor), work.Height)));
    }

    public static PixelRect Initial(PixelRect work, double scale)
    {
        double factor = scale > 0 ? scale : 1;
        (int minWidth, int minHeight) = Minimum(work, factor);
        int width = Fit((int)Math.Round(PreferredWidth * factor), minWidth, work.Width);
        int height = Fit((int)Math.Round(PreferredHeight * factor), minHeight, work.Height);
        return new PixelRect(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
    }

    public static TitleBarFit FitTitleBar(double width, double captionWidth)
    {
        double room = Math.Max(0, width - captionWidth);
        bool admin = room >= AdminFrom;
        bool version = room >= VersionFrom;
        double launcher = room - BrandWidth - TitleBarMargin - (admin ? AdminWidth : 0) - (version ? VersionWidth : 0);
        return new TitleBarFit(Math.Clamp(launcher, LauncherMinWidth, LauncherMaxWidth), version, admin);
    }

    private static int Fit(int preferred, int minimum, int available)
    {
        int share = (int)Math.Floor(available * WorkAreaShare);
        return Math.Min(available, Math.Max(minimum, Math.Min(preferred, share)));
    }
}
