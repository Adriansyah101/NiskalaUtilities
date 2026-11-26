using Microsoft.Extensions.Logging;

namespace Niskala.Scanner;

internal static partial class ScannerLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Read {Count} entries from {Location}")]
    public static partial void UninstallLocationRead(ILogger logger, string location, int count);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Could not read uninstall key {Key}")]
    public static partial void UninstallKeyOpenFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "{Function} failed for {Product}: Win32 error {Error}")]
    public static partial void MsiCallFailed(ILogger logger, string function, string product, int error);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Information, Message = "MSI component index: {Components} components, {Products} wanted products found, {ElapsedMs} ms")]
    public static partial void MsiIndexBuilt(ILogger logger, int components, int products, long elapsedMs);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Debug, Message = "{AppId}: install folder inferred as {Location} from {Paths} component paths")]
    public static partial void LocationInferred(ILogger logger, string appId, string? location, int paths);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Information, Message = "Read {Count} Windows Installer products")]
    public static partial void MsiProductsRead(ILogger logger, int count);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Warning, Message = "Could not read cache {File}")]
    public static partial void CacheReadFailed(ILogger logger, string file, Exception exception);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Warning, Message = "Could not write cache {File}")]
    public static partial void CacheWriteFailed(ILogger logger, string file, Exception exception);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "Inventory: {Total} entries, {Visible} visible, {ElapsedMs} ms")]
    public static partial void InventoryCollected(ILogger logger, int total, int visible, long elapsedMs);

    [LoggerMessage(EventId = 2030, Level = LogLevel.Warning, Message = "Package query failed")]
    public static partial void PackageQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2031, Level = LogLevel.Warning, Message = "Could not read package {Package}")]
    public static partial void PackageReadFailed(ILogger logger, string package, Exception exception);

    [LoggerMessage(EventId = 2032, Level = LogLevel.Information, Message = "Read {Count} packages")]
    public static partial void PackagesRead(ILogger logger, int count);

    [LoggerMessage(EventId = 2040, Level = LogLevel.Debug, Message = "{AppId}: no icon in {Path},{Index}")]
    public static partial void IconNotFound(ILogger logger, string appId, string path, int index);

    [LoggerMessage(EventId = 2041, Level = LogLevel.Warning, Message = "{AppId}: could not write icon")]
    public static partial void IconWriteFailed(ILogger logger, string appId, Exception exception);

    [LoggerMessage(EventId = 2042, Level = LogLevel.Information, Message = "Icon cache pruned: {Deleted} deleted, {Kept} kept")]
    public static partial void IconsPruned(ILogger logger, int deleted, int kept);

    [LoggerMessage(EventId = 2043, Level = LogLevel.Warning, Message = "Could not delete cached icon {File}")]
    public static partial void IconDeleteFailed(ILogger logger, string file, Exception exception);

    [LoggerMessage(EventId = 2043, Level = LogLevel.Debug, Message = "Icon for {AppId} was cached by another request first")]
    public static partial void IconKeptExisting(ILogger logger, string appId, Exception exception);

    [LoggerMessage(EventId = 2050, Level = LogLevel.Warning, Message = "Could not read {Source}")]
    public static partial void TraceSourceFailed(ILogger logger, string source, Exception exception);

    [LoggerMessage(EventId = 2051, Level = LogLevel.Debug, Message = "Could not read {Key}")]
    public static partial void TraceKeyUnreadable(ILogger logger, string key, Exception exception);

    [LoggerMessage(EventId = 2052, Level = LogLevel.Warning, Message = "Task Scheduler unavailable: HRESULT {Error}")]
    public static partial void TaskSchedulerUnavailable(ILogger logger, int error);

    [LoggerMessage(EventId = 2053, Level = LogLevel.Information, Message = "{AppId}: fingerprint with {Files} files and {Traces} system traces, {ElapsedMs} ms")]
    public static partial void FingerprintTaken(ILogger logger, string appId, int files, int traces, long elapsedMs);

    [LoggerMessage(EventId = 2054, Level = LogLevel.Information, Message = "{AppId}: fingerprint saved to {File}")]
    public static partial void FingerprintSaved(ILogger logger, string appId, string file);

    [LoggerMessage(EventId = 2055, Level = LogLevel.Warning, Message = "Could not save the fingerprint of {AppId}")]
    public static partial void FingerprintSaveFailed(ILogger logger, string appId, Exception exception);

    [LoggerMessage(EventId = 2056, Level = LogLevel.Warning, Message = "Could not read stored fingerprint {File}")]
    public static partial void FingerprintReadFailed(ILogger logger, string file, Exception exception);

    [LoggerMessage(EventId = 2060, Level = LogLevel.Information, Message = "{AppId}: {Items} leftovers ({Safe} safe, {Review} to review), {Excluded} excluded, {ElapsedMs} ms")]
    public static partial void LeftoversScanned(ILogger logger, string appId, int items, int safe, int review, int excluded, long elapsedMs);
}
