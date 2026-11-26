using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public enum ActivityKind
{
    Info,
    Success,
    Warning,
    Failure,
    ProcessStarted,
    ProcessExited,
}

public sealed record ActivityRow(DateTime Time, ActivityKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss", Format.Culture);

    public string Glyph => Kind switch
    {
        ActivityKind.Success => "",
        ActivityKind.Warning => "",
        ActivityKind.Failure => "",
        ActivityKind.ProcessStarted => "",
        ActivityKind.ProcessExited => "",
        _ => "",
    };

    public bool IsSuccess => Kind == ActivityKind.Success;

    public bool IsWarning => Kind == ActivityKind.Warning;

    public bool IsFailure => Kind == ActivityKind.Failure;

    public bool IsPlain => Kind is ActivityKind.Info or ActivityKind.ProcessStarted or ActivityKind.ProcessExited;
}
