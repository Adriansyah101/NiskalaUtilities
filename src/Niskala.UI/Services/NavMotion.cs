namespace Niskala.UI.Services;

public static class NavMotion
{
    public const float PressedScale = 0.96f;

    public static TimeSpan Accordion { get; } = TimeSpan.FromMilliseconds(220);

    public static TimeSpan Pane { get; } = TimeSpan.FromMilliseconds(240);

    public static TimeSpan Press { get; } = TimeSpan.FromMilliseconds(110);

    public static TimeSpan Shortest { get; } = TimeSpan.FromMilliseconds(60);

    public static TimeSpan Remaining(double from, double to, double span, TimeSpan full, bool enabled)
    {
        if (!enabled || span <= 0 || !double.IsFinite(from) || !double.IsFinite(to))
        {
            return TimeSpan.Zero;
        }

        double share = Math.Min(1, Math.Abs(to - from) / span);
        return share <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(Shortest.TotalMilliseconds, full.TotalMilliseconds * share));
    }

    public static double AccordionTarget(bool expanded, double natural) => expanded && double.IsFinite(natural) ? Math.Max(0, natural) : 0;

    public static float PressScale(bool pressed, bool enabled) => pressed && enabled ? PressedScale : 1f;

    public static string EmphasisState(bool active, bool open) => active ? "Active" : open ? "Open" : "Plain";

    public static string WidthState(bool compact) => compact ? "Compact" : "Full";

    public static string GuideState(bool guide) => guide ? "Guide" : "NoGuide";
}
