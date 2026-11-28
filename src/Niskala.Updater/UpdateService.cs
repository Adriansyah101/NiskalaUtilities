using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Niskala.Updater;

public sealed class UpdateService : IDisposable
{
    public const string LibraryFolder = "lib";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly GitHubReleaseClient _releases;
    private readonly ReleaseDownloader _downloader;
    private readonly UpdateLauncher _launcher;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
        : this(UpdateHttp.Create(CurrentVersion), SystemUpdateShell.Instance, logger)
    {
    }

    public UpdateService(HttpClient http, IUpdateShell shell, ILogger<UpdateService> logger)
    {
        _http = http;
        _logger = logger;
        _releases = new GitHubReleaseClient(http, CurrentVersion, ReleaseNames.Runtime);
        _downloader = new ReleaseDownloader(http);
        _launcher = new UpdateLauncher(shell);
    }

    public static ReleaseVersion CurrentVersion { get; } = ReleaseVersion.Parse(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    public static string InstallRoot { get; } = RootOf(AppContext.BaseDirectory);

    public static string RootOf(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        string here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        return Path.GetFileName(here).Equals(LibraryFolder, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(here) ?? here : here;
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            UpdateCheck check = await _releases.CheckAsync(timeout.Token).ConfigureAwait(false);
            UpdateLog.Checked(_logger, CurrentVersion.ToString(), check.Latest?.ToString(), check.Status);
            return check;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GitHub did not answer within {CheckTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            UpdateLog.CheckFailed(_logger, ex);
            throw;
        }
    }

    public static ReleaseAsset? PackageFor(UpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Assets.Installer;
    }

    public async Task<VerifiedDownload> DownloadAsync(UpdateCheck check, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ReleaseAsset package = PackageFor(check) ?? throw new UpdateIntegrityException($"Release {check.Latest} has no installer for {ReleaseNames.Runtime}.");
        ReleaseAsset checksums = check.Assets.Checksums ?? throw new UpdateIntegrityException($"Release {check.Latest} has no {ReleaseNames.Checksums}.");
        string folder = ReleaseDownloader.CreateFolder();
        try
        {
            VerifiedDownload download = await _downloader.DownloadAsync(package, checksums, folder, progress, cancellationToken).ConfigureAwait(false);
            UpdateLog.Verified(_logger, package.Name, download.Sha256);
            return download;
        }
        catch (Exception ex)
        {
            if (ex is UpdateIntegrityException)
            {
                UpdateLog.Rejected(_logger, package.Name, ex.Message);
            }

            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    public UpdateLaunch Apply(VerifiedDownload download, bool restart)
    {
        UpdateLaunch launch = _launcher.Launch(download, restart);
        UpdateLog.Applied(_logger, download.Asset.Name);
        return launch;
    }

    public static void Discard(VerifiedDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (Path.GetDirectoryName(download.Path) is { } folder && Path.GetFileName(folder).StartsWith(ReleaseDownloader.FolderPrefix, StringComparison.Ordinal))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public void Dispose() => _http.Dispose();
}

internal static partial class UpdateLog
{
    [LoggerMessage(EventId = 11001, Level = LogLevel.Information, Message = "Update check: running {Current}, latest {Latest}, {Status}")]
    public static partial void Checked(ILogger logger, string current, string? latest, UpdateStatus status);

    [LoggerMessage(EventId = 11002, Level = LogLevel.Warning, Message = "Update check failed")]
    public static partial void CheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 11003, Level = LogLevel.Information, Message = "{Package} downloaded and verified, SHA-256 {Sha256}")]
    public static partial void Verified(ILogger logger, string package, string sha256);

    [LoggerMessage(EventId = 11004, Level = LogLevel.Error, Message = "{Package} rejected: {Reason}")]
    public static partial void Rejected(ILogger logger, string package, string reason);

    [LoggerMessage(EventId = 11005, Level = LogLevel.Information, Message = "Update installer started: {Package}")]
    public static partial void Applied(ILogger logger, string package);
}
