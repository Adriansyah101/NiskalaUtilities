using Microsoft.Extensions.Logging;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Settings;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian;

internal static partial class GuardianLog
{
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Restore point #{Sequence} created")]
    public static partial void RestorePointCreated(ILogger logger, int sequence);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Restore point not created: {Error}")]
    public static partial void RestorePointFailed(ILogger logger, int error);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Transaction {Id} ({Kind}) started for {AppId}")]
    public static partial void TransactionStarted(ILogger logger, string id, TransactionKind kind, string appId);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "Transaction {Id}: {Kind} {Path} -> {Status} {Detail}")]
    public static partial void ItemFinished(ILogger logger, string id, LeftoverKind kind, string path, RemovalStatus status, string? detail);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "Transaction {Id} finished: {Removed} removed, {Scheduled} at restart, {Skipped} skipped, {Failed} failed")]
    public static partial void TransactionFinished(ILogger logger, string id, int removed, int scheduled, int skipped, int failed);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Checkpoint for {AppId} could not be saved")]
    public static partial void CheckpointSaveFailed(ILogger logger, string appId, Exception exception);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Information, Message = "Transaction {Id} restored: {Values} registry values, {Files} files, {Rules} firewall rules")]
    public static partial void TransactionRestored(ILogger logger, string id, int values, int files, int rules);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning, Message = "Transaction {Id} skipped: its record is unreadable")]
    public static partial void TransactionUnreadable(ILogger logger, string id, Exception exception);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Information, Message = "Transaction {Id}: {Kind} {Target} {Name} -> {Status} {Detail}")]
    public static partial void SettingFinished(ILogger logger, string id, SettingKind kind, string target, string? name, SettingStatus status, string? detail);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Transaction {Id} finished: {Files} files, {Bytes} bytes removed, {Kept} kept")]
    public static partial void CleanupFinished(ILogger logger, string id, int files, long bytes, int kept);
}
