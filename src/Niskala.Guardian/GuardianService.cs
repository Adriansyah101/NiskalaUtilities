using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Models;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Guardian.RestorePoints;
using Niskala.Interop.Registry;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Guardian;

public sealed record GuardianCheckpoint(bool CanRollBack, IReadOnlyList<string> Notes, string? TransactionId = null, RestorePointRecord? RestorePoint = null);

public interface IGuardianService
{
    Task<GuardianCheckpoint> PrepareAsync(InstalledApp app, Fingerprint fingerprint, CancellationToken cancellationToken = default);
}

public sealed class GuardianService(
    TransactionStore store,
    IRestorePointService restorePoints,
    ILogger<GuardianService> logger,
    bool createRestorePoint = true,
    TimeProvider? time = null) : IGuardianService
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<GuardianCheckpoint> PrepareAsync(InstalledApp app, Fingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var notes = new List<string>();
        RestorePointRecord? point = null;
        if (createRestorePoint)
        {
            point = await restorePoints.CreateAsync($"Niskala: uninstall {app.DisplayName ?? app.Id}", cancellationToken).ConfigureAwait(false);
            if (point.Note is { } note)
            {
                notes.Add(note);
            }
        }
        else
        {
            notes.Add("No restore point was requested.");
        }

        List<RegistryBackupEntry> backup = BackUp(app, fingerprint, notes);
        var transaction = new TransactionRecord
        {
            SchemaVersion = TransactionStore.SchemaVersion,
            Id = store.NewId(),
            Kind = TransactionKind.Uninstall,
            AppId = app.Id,
            AppName = app.DisplayName,
            StartedAt = _time.GetUtcNow(),
            RestorePoint = point,
            HasRegistryBackup = backup.Count > 0,
            Notes = notes,
        };

        try
        {
            if (backup.Count > 0)
            {
                int unexported = store.SaveRegistryBackup(transaction.Id, new RegistryBackup(backup));
                if (unexported > 0)
                {
                    notes.Add(UnexportedNote(unexported));
                }
            }

            store.Save(transaction);
            GuardianLog.TransactionStarted(logger, transaction.Id, transaction.Kind, app.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GuardianLog.CheckpointSaveFailed(logger, app.Id, ex);
            notes.Add("The registry backup could not be saved.");
            return new GuardianCheckpoint(point?.Created == true, notes, null, point);
        }

        if (backup.Count > 0)
        {
            notes.Add($"{backup.Count} registry entries backed up; niskala restore {transaction.Id} puts them back, and {store.RegistryExportPath(transaction.Id)} imports into Registry Editor.");
        }

        return new GuardianCheckpoint(point?.Created == true, notes, transaction.Id, point);
    }

    internal static string UnexportedNote(int count) =>
        $"{count} registry values have a line break in their name; only the Niskala backup holds them, not the .reg copy.";

    private static List<RegistryBackupEntry> BackUp(InstalledApp app, Fingerprint fingerprint, List<string> notes)
    {
        IEnumerable<string> keys = new[] { app.RegistryKey }
            .Concat(fingerprint.AppPaths.Select(a => a.Key))
            .Concat(fingerprint.Services.Select(s => ServicesKey + s.Name))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var entries = new List<RegistryBackupEntry>();
        foreach (string key in keys)
        {
            if (Open(key, out RegistryPath? path, out SafeRegistryHandle? handle))
            {
                using (handle)
                {
                    if (RegistryEraser.TryCapture(handle!, path!, out string? problem) is { } captured)
                    {
                        entries.Add(new RegistryBackupEntry(key, captured, null));
                    }
                    else
                    {
                        notes.Add($"{key} was not backed up: {problem}.");
                    }
                }
            }
        }

        foreach (RegistryValueTrace run in fingerprint.RunEntries)
        {
            if (Open(run.Key, out _, out SafeRegistryHandle? handle))
            {
                using (handle)
                {
                    string name = run.ValueName ?? string.Empty;
                    if (RegistryNative.TryReadValue(handle!, name, out RawRegistryValue? value) == 0)
                    {
                        entries.Add(new RegistryBackupEntry(run.Key, null, new RegistryValueBackup(name, value!.Kind, value.Data)));
                    }
                }
            }
        }

        return entries;
    }

    private static bool Open(string key, out RegistryPath? path, out SafeRegistryHandle? handle)
    {
        handle = null;
        if (RegistryPath.TryParse(key, RegistryView.Default, out path) != RegistryVerdict.Allowed)
        {
            return false;
        }

        if (RegistryEraser.TryOpen(path!, writable: false, out SafeRegistryHandle opened) != 0)
        {
            opened.Dispose();
            return false;
        }

        handle = opened;
        return true;
    }
}
