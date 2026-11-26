using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Cleaner.Browsers;
using Niskala.Cleaner.Databases;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cleaner.Execution;

public sealed record CleanRunOptions
{
    public required string SubjectId { get; init; }

    public required string SubjectName { get; init; }

    public bool Permanent { get; init; }

    public bool CreateRestorePoint { get; init; } = true;
}

public sealed record RuleOutcome(string RuleId)
{
    public int Removed { get; init; }

    public int Recycled { get; init; }

    public long Bytes { get; init; }

    public int InUse { get; init; }

    public int Refused { get; init; }

    public int Failed { get; init; }

    public int Changed { get; init; }

    public int Folders { get; init; }

    public IReadOnlyList<CompactionResult> Compactions { get; init; } = [];

    public string? Skipped { get; init; }
}

public sealed record CleanRun(TransactionRecord Transaction, IReadOnlyList<RuleOutcome> Rules)
{
    public long Bytes => Rules.Sum(r => r.Bytes);

    public int Files => Rules.Sum(r => r.Removed + r.Recycled);
}

public sealed record CleanRunProgress(string RuleId, int Done, int Total);

public sealed class SystemCleaner(CleanupJournal journal, ICleanEnvironment environment, ILogger<SystemCleaner> logger)
{
    private const int ProgressStep = 64;

    public async Task<CleanRun> CleanAsync(
        IReadOnlyList<RulePreview> previews,
        CleanRunOptions options,
        IProgress<CleanRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(options);
        var request = new CleanupRequest(TransactionKind.SystemClean, options.SubjectId, options.SubjectName)
        {
            CreateRestorePoint = options.CreateRestorePoint,
            Permanent = options.Permanent,
        };
        using CleanupSession session = await journal.BeginAsync(request, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> running = environment.RunningProcessNames();
        var outcomes = new List<RuleOutcome>();
        try
        {
            foreach (RulePreview preview in previews.Where(p => p.Count > 0 || p.NeedsElevation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RuleOutcome outcome = preview switch
                {
                    { NeedsElevation: true } when !environment.IsElevated => new RuleOutcome(preview.Rule.Id) { Skipped = "needs elevation" },
                    { Rule.Action: RuleAction.Compact } => Compact(session, preview, running, progress),
                    _ => Delete(session, preview, running, options.Permanent, progress, cancellationToken),
                };
                outcomes.Add(outcome);
                session.Save();
                CleanerLog.RuleCleaned(logger, session.Transaction.Id, outcome.RuleId, outcome.Removed + outcome.Recycled, outcome.Bytes, outcome.Skipped);
            }
        }
        catch (OperationCanceledException)
        {
            session.Note("Cancelled; the rules after the last one listed were not run.");
            session.Complete();
            throw;
        }

        return new CleanRun(session.Complete(), outcomes);
    }

    private static RuleOutcome Compact(CleanupSession session, RulePreview preview, IReadOnlySet<string> running, IProgress<CleanRunProgress>? progress)
    {
        if (LockedNow(preview, running) is { } locked)
        {
            return new RuleOutcome(preview.Rule.Id) { Skipped = locked };
        }

        var results = new List<CompactionResult>();
        foreach (string database in preview.Databases)
        {
            CompactionResult result = DatabaseCompactor.Compact(database);
            results.Add(result);
            session.Add(new RemovalRecord(LeftoverKind.File, database, null, result.Outcome switch
            {
                CompactionOutcome.Compacted => RemovalStatus.Compacted,
                CompactionOutcome.Failed => RemovalStatus.Failed,
                _ => RemovalStatus.Skipped,
            })
            {
                Bytes = result.Saved,
                Detail = result.Outcome == CompactionOutcome.Compacted
                    ? string.Create(CultureInfo.InvariantCulture, $"{preview.Rule.Id}: {result.BytesBefore} -> {result.BytesAfter} bytes")
                    : $"{preview.Rule.Id}: {result.Outcome} {result.Detail}".TrimEnd(),
            });
            progress?.Report(new CleanRunProgress(preview.Rule.Id, results.Count, preview.Databases.Count));
        }

        return new RuleOutcome(preview.Rule.Id) { Bytes = results.Sum(r => r.Saved), Compactions = results };
    }

    private RuleOutcome Delete(
        CleanupSession session,
        RulePreview preview,
        IReadOnlySet<string> running,
        bool permanent,
        IProgress<CleanRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (LockedNow(preview, running) is { } locked)
        {
            return new RuleOutcome(preview.Rule.Id) { Skipped = locked };
        }

        bool recycle = !permanent && preview.Rule.Removal == RemovalMode.RecycleBin;
        DateTimeOffset now = environment.Now;
        int removed = 0, recycled = 0, inUse = 0, refused = 0, failed = 0, changed = 0;
        long deletedBytes = 0, recycledBytes = 0;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < preview.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanFile file = preview.Files[i];
            if (!StillMatches(file, preview.Rule.MinimumAge, now))
            {
                changed++;
                continue;
            }

            FileRemoval removal = session.RemoveFile(file.Path, recycle);
            switch (removal.Outcome)
            {
                case FileRemovalOutcome.Removed:
                    removed++;
                    deletedBytes += file.Bytes;
                    emptied.Add(Path.GetDirectoryName(file.Path)!);
                    break;
                case FileRemovalOutcome.Recycled:
                    recycled++;
                    recycledBytes += file.Bytes;
                    emptied.Add(Path.GetDirectoryName(file.Path)!);
                    session.Add(new RemovalRecord(LeftoverKind.File, file.Path, null, RemovalStatus.Removed)
                    {
                        RecycledPath = removal.RecycledPath,
                        Bytes = file.Bytes,
                        Detail = $"{preview.Rule.Id}: moved to the Recycle Bin",
                    });
                    break;
                case FileRemovalOutcome.Missing:
                    changed++;
                    break;
                case FileRemovalOutcome.Locked:
                    inUse++;
                    break;
                case FileRemovalOutcome.Refused:
                    refused++;
                    break;
                default:
                    failed++;
                    break;
            }

            if ((i + 1) % ProgressStep == 0 || i + 1 == preview.Files.Count)
            {
                progress?.Report(new CleanRunProgress(preview.Rule.Id, i + 1, preview.Files.Count));
            }
        }

        int folders = RemoveEmptyFolders(session, preview.Folders, emptied);
        if (preview.Folders.Count > 0)
        {
            session.Add(new RemovalRecord(LeftoverKind.Folder, preview.Folders[0].Path, null, removed + recycled > 0 ? RemovalStatus.Removed : RemovalStatus.Skipped)
            {
                Files = removed,
                Bytes = deletedBytes,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{preview.Rule.Id}: {removed} deleted, {recycled} recycled, {inUse} in use, {refused} protected, {failed} failed, {changed} changed since the scan"),
            });
        }

        return new RuleOutcome(preview.Rule.Id)
        {
            Removed = removed,
            Recycled = recycled,
            Bytes = deletedBytes + recycledBytes,
            InUse = inUse,
            Refused = refused,
            Failed = failed,
            Changed = changed,
            Folders = folders,
        };
    }

    private static int RemoveEmptyFolders(CleanupSession session, IReadOnlyList<CleanFolder> roots, HashSet<string> touched)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in touched)
        {
            if (roots.FirstOrDefault(r => r.Recurse && !r.KeepFolders && IsStrictlyUnder(folder, r.Path)) is not { } root)
            {
                continue;
            }

            for (string? current = folder; current is not null && IsStrictlyUnder(current, root.Path); current = Path.GetDirectoryName(current))
            {
                candidates.Add(current);
            }
        }

        return candidates.OrderByDescending(c => c.Length).Count(session.RemoveEmptyFolder);
    }

    private static bool IsStrictlyUnder(string path, string root) =>
        path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\';

    private static bool StillMatches(CleanFile file, TimeSpan minimumAge, DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(file.Path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0
                && CleanScanner.IsOldEnough(now, minimumAge, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? LockedNow(RulePreview preview, IReadOnlySet<string> running)
    {
        if (preview.Rule.Browser is not { } browser)
        {
            return null;
        }

        return preview.Profiles.Any(p => BrowserProfiles.LockState(browser, p, running) != ProfileLockState.Free)
            ? $"{browser.Name} is open; close it and scan again"
            : null;
    }
}
