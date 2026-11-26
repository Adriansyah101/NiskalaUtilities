using Microsoft.Extensions.Logging;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;

namespace Niskala.Core.Diagnostics;

internal static partial class CoreLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Path denied ({Verdict}): {Input} -> {Canonical}; rule: {Rule}")]
    public static partial void PathDenied(ILogger logger, PathVerdict verdict, string input, string? canonical, string? rule);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Protection rule skipped, path is not valid ({Verdict}): {Path}")]
    public static partial void ProtectedRuleSkipped(ILogger logger, string path, PathVerdict verdict);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Could not read the profile list; only the current profile is protected by name")]
    public static partial void ProfileListReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Debug, Message = "Registry {Target} denied ({Verdict}): {Input}; rule: {Rule}")]
    public static partial void RegistryDenied(ILogger logger, string target, RegistryVerdict verdict, string input, string? rule);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Could not read service {Service}")]
    public static partial void ServiceReadFailed(ILogger logger, string service, Exception exception);
}
