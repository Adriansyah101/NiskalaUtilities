using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Files;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Desktop;
using Niskala.Interop.FileSystem;
using Niskala.Interop.Firewall;
using Niskala.Interop.Registry;
using Niskala.Interop.Security;
using Niskala.Interop.Services;
using Niskala.Interop.Shell;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Cleanup;

public sealed record CleanOptions
{
    public TransactionKind Kind { get; init; } = TransactionKind.Clean;

    public bool Permanent { get; init; }

    public bool CreateRestorePoint { get; init; } = true;

    public IReadOnlyList<ValueRevert> Reverts { get; init; } = [];
}

public sealed record ValueRevert(string Key, RawRegistryValue Previous, RawRegistryValue? Expected);

public enum CleanStage
{
    RestorePoint,
    Backup,
    Removing,
}

public sealed record CleanProgress(CleanStage Stage, RemovalRecord? Item = null);

public sealed record CleanResult(TransactionRecord Transaction, IReadOnlyList<string> EmptyParents);

public sealed class LeftoverCleaner(
    PathGuard pathGuard,
    RegistryGuard registryGuard,
    TransactionStore store,
    IRestorePointService restorePoints,
    IFirewallEditor firewall,
    IScheduledTaskEditor tasks,
    IServiceControl services,
    ICommandEnvironment environment,
    EmptyParentFinder parents,
    ILogger<LeftoverCleaner> logger,
    TimeProvider? time = null)
{
    private const int ErrorAccessDenied = 5;
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";
    private static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly FileEraser _eraser = new(pathGuard);

    public IReadOnlyList<string> PredictEmptyParents(IReadOnlyList<LeftoverItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return parents.Find(items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.File).Select(i => i.Path));
    }

    public async Task<CleanResult> CleanAsync(
        InstalledApp app,
        Fingerprint fingerprint,
        IReadOnlyList<LeftoverItem> items,
        CleanOptions options,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var records = items.Select(i => new RemovalRecord(i.Kind, i.Path, i.ValueName, RemovalStatus.Pending) { Score = i.Score })
            .Concat(options.Reverts.Select(r => new RemovalRecord(LeftoverKind.RegistryValue, r.Key, r.Previous.Name, RemovalStatus.Pending)))
            .ToArray();
        var transaction = new TransactionRecord
        {
            SchemaVersion = TransactionStore.SchemaVersion,
            Id = store.NewId(),
            Kind = options.Kind,
            AppId = app.Id,
            AppName = app.DisplayName,
            StartedAt = _time.GetUtcNow(),
            Method = options.Permanent ? RemovalMethod.Permanent : RemovalMethod.RecycleBin,
            Items = records,
        };
        store.Save(transaction);
        GuardianLog.TransactionStarted(logger, transaction.Id, transaction.Kind, app.Id);

        if (options.CreateRestorePoint)
        {
            progress?.Report(new CleanProgress(CleanStage.RestorePoint));
            RestorePointRecord point = await restorePoints.CreateAsync($"Niskala: remove {app.DisplayName ?? app.Id}", cancellationToken).ConfigureAwait(false);
            transaction = transaction with { RestorePoint = point };
            store.Save(transaction);
        }

        progress?.Report(new CleanProgress(CleanStage.Backup));
        var owner = new OwnershipMatcher(fingerprint.Roots, environment);
        (List<RegistryBackupEntry> backup, List<FirewallRule> rules, List<ScheduledTaskXml> definitions) = BackUp(items, records, fingerprint, owner);
        int unexported = backup.Count > 0 ? store.SaveRegistryBackup(transaction.Id, new RegistryBackup(backup)) : 0;
        transaction = transaction with
        {
            HasRegistryBackup = backup.Count > 0,
            FirewallRules = rules,
            ScheduledTasks = definitions,
            Items = [.. records],
            Notes = unexported > 0 ? [.. transaction.Notes, GuardianService.UnexportedNote(unexported)] : transaction.Notes,
        };
        store.Save(transaction);

        using (options.Permanent ? PrivilegeScope.Enable(SystemPrivilege.Backup, SystemPrivilege.Restore) : null)
        {
            foreach (int index in RemovalOrder(items))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records[index].Status != RemovalStatus.Pending)
                {
                    continue;
                }

                records[index] = Remove(items[index], records[index], rules, options.Permanent);
                transaction = transaction with { Items = [.. records] };
                store.Save(transaction);
                progress?.Report(new CleanProgress(CleanStage.Removing, records[index]));
                GuardianLog.ItemFinished(logger, transaction.Id, records[index].Kind, records[index].Path, records[index].Status, records[index].Detail);
            }
        }

        for (int i = 0; i < options.Reverts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = items.Count + i;
            records[index] = Revert(options.Reverts[i], records[index]);
            transaction = transaction with { Items = [.. records] };
            store.Save(transaction);
            progress?.Report(new CleanProgress(CleanStage.Removing, records[index]));
            GuardianLog.ItemFinished(logger, transaction.Id, records[index].Kind, records[index].Path, records[index].Status, records[index].Detail);
        }

        if (records.Any(r => r.Status == RemovalStatus.Reverted && r.Path.EndsWith(@"\Environment", StringComparison.OrdinalIgnoreCase)))
        {
            WindowNative.BroadcastSettingChange("Environment");
        }

        transaction = transaction with { FinishedAt = _time.GetUtcNow() };
        store.Save(transaction);
        GuardianLog.TransactionFinished(
            logger,
            transaction.Id,
            records.Count(r => r.Status == RemovalStatus.Removed),
            records.Count(r => r.Status == RemovalStatus.ScheduledAtReboot),
            records.Count(r => r.Status == RemovalStatus.Skipped),
            records.Count(r => r.Status == RemovalStatus.Failed));

        IReadOnlyList<string> empty = parents.Find(records
            .Where(r => r.Kind is LeftoverKind.Folder or LeftoverKind.File && r.Status == RemovalStatus.Removed)
            .Select(r => r.Path));
        return new CleanResult(transaction, empty);
    }

    public TransactionRecord RemoveEmptyFolders(string transactionId, IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        TransactionRecord transaction = store.Load(transactionId) ?? throw new InvalidOperationException($"No transaction {transactionId}.");
        var added = new List<RemovalRecord>();
        foreach (string folder in folders)
        {
            var record = new RemovalRecord(LeftoverKind.Folder, folder, null, RemovalStatus.Pending) { EmptyFolder = true };
            FileCheck check = _eraser.Check(folder, expectDirectory: true, out CheckedEntry? entry, out string detail);
            using (entry)
            {
                record = check switch
                {
                    FileCheck.Allowed => FileEraser.TryRemoveEmptyFolder(entry!) switch
                    {
                        0 => record with { Status = RemovalStatus.Removed, Detail = "empty folder" },
                        FileTreeNative.ErrorDirNotEmpty => record with { Status = RemovalStatus.Skipped, Detail = "no longer empty" },
                        int error => record with { Status = RemovalStatus.Failed, Detail = string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}") },
                    },
                    FileCheck.Missing => record with { Status = RemovalStatus.Skipped, Detail = detail },
                    FileCheck.Failed or FileCheck.Locked => record with { Status = RemovalStatus.Failed, Detail = detail },
                    _ => record with { Status = RemovalStatus.Skipped, Detail = detail },
                };
            }

            added.Add(record);
            GuardianLog.ItemFinished(logger, transaction.Id, record.Kind, record.Path, record.Status, record.Detail);
        }

        transaction = transaction with { Items = [.. transaction.Items, .. added] };
        store.Save(transaction);
        return transaction;
    }

    private static IEnumerable<int> RemovalOrder(IReadOnlyList<LeftoverItem> items) => Enumerable.Range(0, items.Count).OrderBy(i => items[i].Kind switch
    {
        LeftoverKind.RegistryKey when items[i].Path.StartsWith(ServicesKey, StringComparison.OrdinalIgnoreCase) => 0,
        LeftoverKind.FirewallRule or LeftoverKind.ScheduledTask => 1,
        LeftoverKind.RegistryValue => 2,
        LeftoverKind.RegistryKey => 3,
        _ => 4,
    });

    private (List<RegistryBackupEntry> Backup, List<FirewallRule> Rules, List<ScheduledTaskXml> Tasks) BackUp(
        IReadOnlyList<LeftoverItem> items,
        RemovalRecord[] records,
        Fingerprint fingerprint,
        OwnershipMatcher owner)
    {
        var backup = new List<RegistryBackupEntry>();
        var rules = new List<FirewallRule>();
        var definitions = new List<ScheduledTaskXml>();
        List<FirewallRule>? current = null;
        int listError = 0;
        for (int i = 0; i < items.Count; i++)
        {
            LeftoverItem item = items[i];
            switch (item.Kind)
            {
                case LeftoverKind.RegistryKey or LeftoverKind.RegistryValue:
                    (RegistryBackupEntry? entry, RemovalRecord? refusal) = BackUpRegistry(item, records[i]);
                    if (entry is not null)
                    {
                        backup.Add(entry);
                    }
                    else
                    {
                        records[i] = refusal!;
                    }

                    break;
                case LeftoverKind.FirewallRule:
                    if (current is null)
                    {
                        listError = firewall.TryList(out current);
                    }

                    List<FirewallRule> named = [.. current.Where(r => r.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase))];
                    if (listError != 0)
                    {
                        records[i] = Failed(records[i], string.Create(CultureInfo.InvariantCulture, $"firewall rules could not be read (0x{listError:X8})"));
                    }
                    else if (named.Count == 0)
                    {
                        records[i] = Skipped(records[i], "already gone");
                    }
                    else if (!named.TrueForAll(r => owner.OwnsPath(r.ApplicationName) || fingerprint.Services.Any(s => s.Name.Equals(r.ServiceName, StringComparison.OrdinalIgnoreCase))))
                    {
                        records[i] = Skipped(records[i], "the name now also covers rules for other programs");
                    }
                    else
                    {
                        rules.AddRange(named);
                    }

                    break;
                case LeftoverKind.ScheduledTask:
                    int readError = tasks.TryRead(item.Path, out string? xml);
                    if (readError == TaskSchedulerNative.NotFound)
                    {
                        records[i] = Skipped(records[i], "already gone");
                    }
                    else if (readError != 0 || xml is null)
                    {
                        records[i] = Failed(records[i], readError == TaskSchedulerNative.AccessDenied
                            ? "reading this task needs elevation"
                            : string.Create(CultureInfo.InvariantCulture, $"the task could not be read (0x{readError:X8})"));
                    }
                    else if (!ScheduledTaskLeftoverProbe.StillLeftover(item.Path, xml, owner, environment))
                    {
                        records[i] = Skipped(records[i], "the task now starts a program the app does not own");
                    }
                    else
                    {
                        definitions.Add(new ScheduledTaskXml(item.Path, xml));
                    }

                    break;
            }
        }

        return (backup, rules, definitions);
    }

    private (RegistryBackupEntry? Entry, RemovalRecord? Refusal) BackUpRegistry(LeftoverItem item, RemovalRecord record)
    {
        bool isKey = item.Kind == LeftoverKind.RegistryKey;
        RegistryDecision decision = isKey ? registryGuard.CheckKey(item.Path) : registryGuard.CheckValue(item.Path, item.ValueName);
        if (!decision.IsAllowed)
        {
            return (null, Skipped(record, decision.ToString()));
        }

        RegistryPath path = decision.Path!;
        int error = RegistryEraser.TryOpen(path, writable: false, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return (null, error == RegistryNative.ErrorFileNotFound ? Skipped(record, "already gone") : Failed(record, RegistryFailure(error)));
            }

            if (isKey)
            {
                return RegistryEraser.TryCapture(key, path, out string? problem) is { } captured
                    ? (new RegistryBackupEntry(item.Path, captured, null), null)
                    : (null, Skipped(record, problem ?? "unreadable"));
            }

            if (RegistryNative.IsLink(key))
            {
                return (null, Skipped(record, "the key is a registry link"));
            }

            string name = item.ValueName ?? string.Empty;
            error = RegistryNative.TryReadValue(key, name, out RawRegistryValue? value);
            return error == 0
                ? (new RegistryBackupEntry(item.Path, null, new RegistryValueBackup(name, value!.Kind, value.Data)), null)
                : (null, error == RegistryNative.ErrorFileNotFound ? Skipped(record, "already gone") : Failed(record, RegistryFailure(error)));
        }
    }

    private RemovalRecord Remove(LeftoverItem item, RemovalRecord record, List<FirewallRule> rules, bool permanent) => item.Kind switch
    {
        LeftoverKind.Folder or LeftoverKind.File => RemoveFile(item, record, permanent),
        LeftoverKind.RegistryKey => RemoveKey(item, record),
        LeftoverKind.RegistryValue => RemoveValue(item, record),
        LeftoverKind.FirewallRule => RemoveRule(item, record, rules),
        _ => RemoveTask(item, record),
    };

    private RemovalRecord RemoveFile(LeftoverItem item, RemovalRecord record, bool permanent)
    {
        FileCheck check = _eraser.Check(item.Path, item.Kind == LeftoverKind.Folder, out CheckedEntry? entry, out string detail);
        using (entry)
        {
            return check switch
            {
                FileCheck.Allowed when permanent => Erased(record, FileEraser.Erase(entry!)),
                FileCheck.Allowed => Recycled(record, entry!),
                FileCheck.Locked when permanent => FileTreeNative.TryDeleteAtReboot(item.Path) == 0
                    ? record with { Status = RemovalStatus.ScheduledAtReboot, Detail = "in use; deleted at the next start" }
                    : Failed(record, "in use; deleting it at the next start needs elevation"),
                FileCheck.Locked => Failed(record, "in use; close the program, or use --permanent to delete it at the next start"),
                FileCheck.Failed => Failed(record, detail),
                _ => Skipped(record, detail),
            };
        }
    }

    private static RemovalRecord Erased(RemovalRecord record, EraseOutcome outcome) => outcome switch
    {
        { Kept: > 0 } => Failed(record, string.Create(CultureInfo.InvariantCulture, $"{outcome.Kept} entries kept, {outcome.Deleted} deleted; {outcome.Problem}")),
        { Scheduled: > 0 } => record with
        {
            Status = RemovalStatus.ScheduledAtReboot,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{outcome.Deleted} entries deleted, {outcome.Scheduled} in use and deleted at the next start"),
        },
        _ => record with { Status = RemovalStatus.Removed, Detail = string.Create(CultureInfo.InvariantCulture, $"{outcome.Deleted} entries deleted") },
    };

    internal static RemovalRecord Recycled(RemovalRecord record, CheckedEntry entry)
    {
        RecycleResult result = RecycleBinNative.Recycle(entry.Path);
        switch (result.Status)
        {
            case RecycleStatus.Recycled:
                string? now = FileTreeNative.TryGetFinalPath(entry.Handle, out string moved) == 0 ? moved : null;
                if (result.RecycledPath is { } reported && now is not null && !reported.Equals(now, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed(record, $"the Recycle Bin received a different object ({reported}); restore it from there") with { RecycledPath = reported };
                }

                return record with { Status = RemovalStatus.Removed, RecycledPath = result.RecycledPath ?? now, Detail = "moved to the Recycle Bin" };
            case RecycleStatus.NotRecyclable:
                return Failed(record, "cannot go to the Recycle Bin (none on this drive, or too large); --permanent deletes it");
            default:
                return Failed(record, result.HResult == SharingViolationHResult
                    ? "in use; close the program, or use --permanent to delete it at the next start"
                    : string.Create(CultureInfo.InvariantCulture, $"the shell refused (0x{result.HResult:X8})"));
        }
    }

    private RemovalRecord RemoveKey(LeftoverItem item, RemovalRecord record)
    {
        RegistryDecision decision = registryGuard.CheckKey(item.Path);
        if (!decision.IsAllowed)
        {
            return Skipped(record, decision.ToString());
        }

        if (item.Path.StartsWith(ServicesKey, StringComparison.OrdinalIgnoreCase)
            && item.Path[ServicesKey.Length..] is { Length: > 0 } name && !name.Contains('\\')
            && services.TryQuery(name, out ServiceState state) == 0 && state != ServiceState.NotFound)
        {
            return RemoveService(record, name, state);
        }

        int error = RegistryEraser.TryOpen(decision.Path!, writable: true, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error == RegistryNative.ErrorFileNotFound ? Skipped(record, "already gone") : Failed(record, RegistryFailure(error));
            }

            error = RegistryEraser.TryDeleteTree(key, decision.Path!, out string? problem);
            return error == 0 ? record with { Status = RemovalStatus.Removed } : Failed(record, problem ?? RegistryFailure(error));
        }
    }

    private RemovalRecord RemoveService(RemovalRecord record, string name, ServiceState state)
    {
        if (state != ServiceState.Stopped)
        {
            int stopped = services.TryStop(name, ServiceStopTimeout);
            if (stopped != 0)
            {
                return Failed(record, stopped == ErrorAccessDenied
                    ? "stopping the service needs elevation"
                    : string.Create(CultureInfo.InvariantCulture, $"the service did not stop (Win32 error {stopped})"));
            }
        }

        return services.TryDelete(name) switch
        {
            0 => record with { Status = RemovalStatus.Removed, Detail = "service deleted through the Service Control Manager" },
            ServiceNative.ErrorServiceMarkedForDelete => record with { Status = RemovalStatus.ScheduledAtReboot, Detail = "service already marked for deletion" },
            ErrorAccessDenied => Failed(record, "deleting the service needs elevation"),
            int error => Failed(record, string.Create(CultureInfo.InvariantCulture, $"the service was not deleted (Win32 error {error})")),
        };
    }

    private RemovalRecord RemoveValue(LeftoverItem item, RemovalRecord record)
    {
        RegistryDecision decision = registryGuard.CheckValue(item.Path, item.ValueName);
        if (!decision.IsAllowed)
        {
            return Skipped(record, decision.ToString());
        }

        int error = RegistryEraser.TryOpen(decision.Path!, writable: true, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error == RegistryNative.ErrorFileNotFound ? Skipped(record, "already gone") : Failed(record, RegistryFailure(error));
            }

            if (RegistryNative.IsLink(key))
            {
                return Skipped(record, "the key is a registry link");
            }

            return RegistryNative.TryDeleteValue(key, item.ValueName ?? string.Empty) switch
            {
                0 => record with { Status = RemovalStatus.Removed },
                RegistryNative.ErrorFileNotFound => Skipped(record, "already gone"),
                int failed => Failed(record, RegistryFailure(failed)),
            };
        }
    }

    private RemovalRecord Revert(ValueRevert revert, RemovalRecord record)
    {
        RegistryDecision decision = registryGuard.CheckRevert(
            revert.Key,
            revert.Previous.Name,
            (RegistryValueKind)revert.Previous.Kind,
            revert.Expected is { } expected ? (RegistryValueKind)expected.Kind : null);
        if (!decision.IsAllowed)
        {
            return Skipped(record, decision.ToString());
        }

        int error = RegistryEraser.TryOpen(decision.Path!, writable: true, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error == RegistryNative.ErrorFileNotFound ? Skipped(record, "the key is gone") : Failed(record, RegistryFailure(error));
            }

            if (RegistryNative.IsLink(key))
            {
                return Skipped(record, "the key is a registry link");
            }

            error = RegistryNative.TryReadValue(key, revert.Previous.Name, out RawRegistryValue? current);
            if (error is not (0 or RegistryNative.ErrorFileNotFound))
            {
                return Failed(record, RegistryFailure(error));
            }

            if (!SameValue(current, revert.Expected))
            {
                return Skipped(record, "changed since the install");
            }

            error = RegistryNative.TrySetValue(key, revert.Previous);
            return error == 0
                ? record with { Status = RemovalStatus.Reverted, Detail = "earlier data written back" }
                : Failed(record, RegistryFailure(error));
        }
    }

    private static bool SameValue(RawRegistryValue? current, RawRegistryValue? expected) => (current, expected) switch
    {
        (null, null) => true,
        ({ } a, { } b) => a.Kind == b.Kind && a.Data.AsSpan().SequenceEqual(b.Data),
        _ => false,
    };

    private RemovalRecord RemoveRule(LeftoverItem item, RemovalRecord record, List<FirewallRule> rules)
    {
        int count = rules.Count(r => r.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < count; i++)
        {
            int error = firewall.TryRemove(item.Path);
            if (error != 0)
            {
                return Failed(record, error == AccessDeniedHResult
                    ? "removing firewall rules needs elevation"
                    : string.Create(CultureInfo.InvariantCulture, $"firewall error 0x{error:X8}"));
            }
        }

        return record with { Status = RemovalStatus.Removed, Detail = string.Create(CultureInfo.InvariantCulture, $"{count} rules removed") };
    }

    private RemovalRecord RemoveTask(LeftoverItem item, RemovalRecord record) => tasks.TryDelete(item.Path) switch
    {
        0 => record with { Status = RemovalStatus.Removed, Detail = "task deleted; its definition is in the journal" },
        TaskSchedulerNative.NotFound => Skipped(record, "already gone"),
        TaskSchedulerNative.AccessDenied => Failed(record, "removing this task needs elevation"),
        int error => Failed(record, string.Create(CultureInfo.InvariantCulture, $"Task Scheduler error 0x{error:X8}")),
    };

    private static string RegistryFailure(int error) => error == ErrorAccessDenied
        ? "access denied; machine-wide keys need elevation"
        : string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}");

    private static RemovalRecord Skipped(RemovalRecord record, string detail) => record with { Status = RemovalStatus.Skipped, Detail = detail };

    private static RemovalRecord Failed(RemovalRecord record, string detail) => record with { Status = RemovalStatus.Failed, Detail = detail };
}
