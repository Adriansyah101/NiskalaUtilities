using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Niskala.Interop.Etw;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Launching;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Tracing;

namespace Niskala.Tracker;

public sealed record TrackRequest(string Installer, string? Arguments, string Name);

public enum TrackStage
{
    Running,
    Settling,
    Reconciling,
}

public sealed record TrackProgress(TrackStage Stage, RecordedProcess? Process = null);

public sealed partial class InstallTracker(
    ITraceSource trace,
    IInstallerLauncher launcher,
    ISystemState state,
    ISystemServices services,
    NoiseFilter filter,
    InstallLogStore store,
    KernelPathTranslator paths,
    ILogger<InstallTracker> logger,
    TimeSpan? settleDelay = null,
    TimeProvider? time = null)
{
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<InstallLog> RunAsync(TrackRequest request, IProgress<TrackProgress>? progress, CancellationToken finish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedAt = _time.GetUtcNow();
        ServiceSnapshot before = services.Capture();
        var recorder = new InstallRecorder(paths, new RecorderOptions(services.ServicesProcessId(), services.InstallerServiceProcessId, services.ImagePath), _time);
        if (progress is not null)
        {
            recorder.ProcessChanged += p => progress.Report(new TrackProgress(TrackStage.Running, p));
        }

        using ITraceSession session = trace.Start(recorder.OnEvent);
        using ILaunchedInstaller installer = launcher.Start(request.Installer, request.Arguments);
        recorder.AddRoot(installer.ProcessId, installer.ImagePath, session.Track);
        installer.Resume();
        TrackerLog.Started(logger, request.Installer, installer.ProcessId);

        bool stoppedEarly = false;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, finish))
        {
            try
            {
                await installer.WaitForTreeAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stoppedEarly = true;
            }
        }

        progress?.Report(new TrackProgress(TrackStage.Settling));
        await Task.Delay(settleDelay ?? DefaultSettleDelay, _time, cancellationToken).ConfigureAwait(false);
        EtwStats stats = session.Stop();

        progress?.Report(new TrackProgress(TrackStage.Reconciling));
        RecordedTrace recorded = recorder.Snapshot();
        ReconciledChanges changes = ChangeReconciler.Reconcile(recorded.Events, state);
        (IReadOnlyList<KeyChange> serviceKeys, IReadOnlyList<FirewallRuleChange> rules) = ServiceChanges.Find(before, services.Capture(), changes.Files, services);
        changes = changes with { Keys = [.. changes.Keys, .. serviceKeys.Where(s => !changes.Keys.Any(k => k.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase)))] };
        FilteredChanges kept = filter.Apply(changes);

        IReadOnlyList<RecordedProcess> running = stoppedEarly ? recorder.Running() : [];
        (string? uninstallKey, string? appId) = FindUninstallEntry(kept.Changes.Keys);
        var log = new InstallLog
        {
            Format = InstallLog.FormatName,
            SchemaVersion = InstallLogStore.SchemaVersion,
            Id = store.NewId(),
            Name = request.Name,
            Installer = Path.GetFullPath(request.Installer),
            Arguments = request.Arguments,
            StartedAt = startedAt,
            FinishedAt = _time.GetUtcNow(),
            ExitCode = installer.ExitCode,
            UninstallKey = uninstallKey,
            AppId = appId,
            Processes = recorded.Processes,
            Files = kept.Changes.Files,
            Keys = kept.Changes.Keys,
            Values = kept.Changes.Values,
            FirewallRules = rules,
            Filtered = kept.Filtered,
            Statistics = new TraceStatistics(stats.EventsSeen, stats.EventsForwarded, stats.EventsLost, stats.BuffersLost, stats.PendingDropped, stats.UnnamedEvents, recorded.UnmappedPaths),
        };

        log = log with { Notes = Notes(log, stats, running) };
        store.Save(log);
        TrackerLog.Saved(logger, log.Id, log.Files.Count, log.Keys.Count, log.Values.Count, stats.EventsLost);
        return log;
    }

    private static List<string> Notes(InstallLog log, EtwStats stats, IReadOnlyList<RecordedProcess> running)
    {
        var notes = new List<string>();
        if (stats.EventsLost + stats.BuffersLost > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The kernel dropped {stats.EventsLost} events and {stats.BuffersLost} buffers while recording; changes may be missing."));
        }

        if (stats.PendingDropped > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{stats.PendingDropped} events came from processes whose start was not seen and are left out."));
        }

        if (stats.UnnamedEvents > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{stats.UnnamedEvents} registry changes could not be named and are left out."));
        }

        if (log.Statistics?.UnmappedPaths is > 0 and var unmapped)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{unmapped} changes outside the local drives and the machine and user hives are left out."));
        }

        if (running.Count > 0)
        {
            notes.Add($"Recording stopped while these were still running: {string.Join(", ", running.Select(p => Path.GetFileName(p.ImagePath)))}.");
        }

        if (log.UsesWindowsInstaller())
        {
            notes.Add("Windows Installer did part of this install; its own records of the product are not in the log.");
        }

        return notes;
    }

    private static (string? Key, string? AppId) FindUninstallEntry(IReadOnlyList<KeyChange> keys)
    {
        foreach (KeyChange key in keys.Where(k => k.Change == ChangeKind.Created))
        {
            Match match = UninstallKey().Match(key.Path);
            if (match.Success)
            {
                string location = match.Groups["root"].Value.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "hkcu"
                    : match.Groups["wow"].Success ? "hklm32"
                    : "hklm64";
                return (key.Path, $"reg:{location}:{match.Groups["name"].Value}");
            }
        }

        return (null, null);
    }

    [GeneratedRegex(@"^(?<root>HKLM\\SOFTWARE|HKCU\\Software)(?<wow>\\WOW6432Node)?\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\(?<name>[^\\]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UninstallKey();
}

internal static partial class TrackerLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Recording install {Installer} (process {ProcessId})")]
    public static partial void Started(ILogger logger, string installer, int processId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Install log {Id} saved: {Files} files, {Keys} keys, {Values} values; {Lost} events lost")]
    public static partial void Saved(ILogger logger, string id, int files, int keys, int values, int lost);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Install log {Id} rolled back in transaction {TransactionId}")]
    public static partial void RolledBack(ILogger logger, string id, string transactionId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Warning, Message = "Install log {File} skipped: it is unreadable")]
    public static partial void InstallLogUnreadable(ILogger logger, string file, Exception exception);
}
