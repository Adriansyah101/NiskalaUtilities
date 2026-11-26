using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Safety;
using Niskala.Guardian.Files;
using Niskala.Guardian.Journal;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.FileSystem;
using Niskala.Interop.Security;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Cleanup;

public enum FileRemovalOutcome
{
    Removed,
    Recycled,
    Missing,
    Locked,
    Refused,
    Failed,
}

public readonly record struct FileRemoval(FileRemovalOutcome Outcome, string? Detail = null, string? RecycledPath = null)
{
    public bool IsGone => Outcome is FileRemovalOutcome.Removed or FileRemovalOutcome.Recycled or FileRemovalOutcome.Missing;
}

public sealed record CleanupRequest(TransactionKind Kind, string SubjectId, string SubjectName)
{
    public bool CreateRestorePoint { get; init; } = true;

    public bool Permanent { get; init; }
}

public sealed class CleanupJournal(PathGuard guard, TransactionStore store, IRestorePointService restorePoints, ILogger<CleanupJournal> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CleanupSession> BeginAsync(CleanupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transaction = new TransactionRecord
        {
            SchemaVersion = TransactionStore.SchemaVersion,
            Id = store.NewId(),
            Kind = request.Kind,
            AppId = request.SubjectId,
            AppName = request.SubjectName,
            StartedAt = _time.GetUtcNow(),
            Method = request.Permanent ? RemovalMethod.Permanent : RemovalMethod.RecycleBin,
        };
        store.Save(transaction);
        GuardianLog.TransactionStarted(logger, transaction.Id, transaction.Kind, request.SubjectId);

        if (request.CreateRestorePoint)
        {
            RestorePointRecord point = await restorePoints.CreateAsync($"Niskala: {request.SubjectName}", cancellationToken).ConfigureAwait(false);
            transaction = transaction with { RestorePoint = point, Notes = point.Note is { } note ? [note] : [] };
            store.Save(transaction);
        }

        return new CleanupSession(new FileEraser(guard), store, transaction, logger, _time);
    }
}

public sealed class CleanupSession : IDisposable
{
    private readonly FileEraser _eraser;
    private readonly TransactionStore _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly List<RemovalRecord> _items = [];
    private readonly List<string> _notes;
    private readonly PrivilegeScope? _privileges;
    private TransactionRecord _transaction;

    internal CleanupSession(FileEraser eraser, TransactionStore store, TransactionRecord transaction, ILogger logger, TimeProvider time)
    {
        _eraser = eraser;
        _store = store;
        _transaction = transaction;
        _logger = logger;
        _time = time;
        _notes = [.. transaction.Notes];
        _privileges = Environment.IsPrivilegedProcess ? PrivilegeScope.Enable(SystemPrivilege.Backup, SystemPrivilege.Restore) : null;
    }

    public TransactionRecord Transaction => _transaction;

    public FileRemoval RemoveFile(string path, bool recycle)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileCheck check = _eraser.Check(path, expectDirectory: false, out CheckedEntry? entry, out string detail);
        using (entry)
        {
            if (check == FileCheck.Allowed && (PathSyntax.TryNormalize(path, out string requested) != PathVerdict.Allowed
                || !string.Equals(requested, entry!.Path, StringComparison.OrdinalIgnoreCase)))
            {
                return new FileRemoval(FileRemovalOutcome.Refused, $"the path now leads to {entry!.Path}");
            }

            return check switch
            {
                FileCheck.Allowed when recycle => Recycle(entry!),
                FileCheck.Allowed => FileTreeNative.TryMarkForDeletion(entry!.Handle) switch
                {
                    0 => new FileRemoval(FileRemovalOutcome.Removed),
                    FileTreeNative.ErrorSharingViolation => new FileRemoval(FileRemovalOutcome.Locked, "in use"),
                    FileTreeNative.ErrorAccessDenied => new FileRemoval(FileRemovalOutcome.Failed, "access denied"),
                    int error => new FileRemoval(FileRemovalOutcome.Failed, string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}")),
                },
                FileCheck.Missing => new FileRemoval(FileRemovalOutcome.Missing, detail),
                FileCheck.Locked => new FileRemoval(FileRemovalOutcome.Locked, detail),
                FileCheck.Failed => new FileRemoval(FileRemovalOutcome.Failed, detail),
                _ => new FileRemoval(FileRemovalOutcome.Refused, detail),
            };
        }
    }

    public bool RemoveEmptyFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_eraser.Check(path, expectDirectory: true, out CheckedEntry? entry, out _) != FileCheck.Allowed)
        {
            return false;
        }

        using (entry)
        {
            return FileEraser.TryRemoveEmptyFolder(entry!) == 0;
        }
    }

    public void Add(RemovalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _items.Add(record);
        GuardianLog.ItemFinished(_logger, _transaction.Id, record.Kind, record.Path, record.Status, record.Detail);
    }

    public void Note(string note)
    {
        ArgumentException.ThrowIfNullOrEmpty(note);
        _notes.Add(note);
    }

    public void Save()
    {
        _transaction = _transaction with { Items = [.. _items], Notes = [.. _notes] };
        _store.Save(_transaction);
    }

    public TransactionRecord Complete()
    {
        _transaction = _transaction with { Items = [.. _items], Notes = [.. _notes], FinishedAt = _time.GetUtcNow() };
        _store.Save(_transaction);
        GuardianLog.CleanupFinished(
            _logger,
            _transaction.Id,
            _items.Sum(i => i.Files ?? (i.Status == RemovalStatus.Removed ? 1 : 0)),
            _items.Sum(i => i.Bytes ?? 0),
            _items.Count(i => i.Status is RemovalStatus.Failed or RemovalStatus.Skipped));
        return _transaction;
    }

    public void Dispose() => _privileges?.Dispose();

    private static FileRemoval Recycle(CheckedEntry entry)
    {
        RemovalRecord recycled = LeftoverCleaner.Recycled(new RemovalRecord(LeftoverKind.File, entry.Path, null, RemovalStatus.Pending), entry);
        return recycled.Status == RemovalStatus.Removed
            ? new FileRemoval(FileRemovalOutcome.Recycled, recycled.Detail, recycled.RecycledPath)
            : new FileRemoval(FileRemovalOutcome.Failed, recycled.Detail, recycled.RecycledPath);
    }
}
