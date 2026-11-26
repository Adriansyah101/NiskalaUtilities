using Microsoft.Extensions.Logging;

namespace Niskala.UI.Services;

internal static partial class UiLog
{
    [LoggerMessage(EventId = 8001, Level = LogLevel.Critical, Message = "Unhandled UI exception")]
    public static partial void Unhandled(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning, Message = "Settings could not be saved")]
    public static partial void SettingsSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Debug, Message = "Icon for {AppId} could not be loaded")]
    public static partial void IconLoadFailed(ILogger logger, string appId, Exception exception);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Warning, Message = "A page task failed")]
    public static partial void TaskFailed(ILogger logger, Exception exception);
}
