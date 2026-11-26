using Microsoft.Extensions.Logging;
using Niskala.Cleaner.Registry;

namespace Niskala.Cleaner;

internal static partial class CleanerLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Transaction {Id}: rule {Rule} removed {Files} files, {Bytes} bytes {Skipped}")]
    public static partial void RuleCleaned(ILogger logger, string id, string rule, int files, long bytes, string? skipped);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Registry Integrity found {Count} issues in {Elapsed}")]
    public static partial void IntegrityScanned(ILogger logger, int count, TimeSpan elapsed);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Registry Integrity check {Kind} could not read {Path}: error {Error}")]
    public static partial void IntegrityUnreadable(ILogger logger, IntegrityIssueKind kind, string path, int error);
}
