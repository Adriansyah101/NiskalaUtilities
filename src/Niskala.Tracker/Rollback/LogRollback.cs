using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Interop.Registry;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;

namespace Niskala.Tracker.Rollback;

public sealed record RollbackPlan(
    InstallLog Log,
    IReadOnlyList<LeftoverItem> Items,
    IReadOnlyList<ValueRevert> Reverts,
    IReadOnlyList<string> Kept,
    int Gone,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> Roots { get; init; } = [];
}

public sealed class LogRollback(LeftoverCleaner cleaner, InstallLogStore logs, ISystemState state, ILogger<LogRollback> logger, TimeProvider? time = null)
{
    private const int FullScore = 100;
    private static readonly LeftoverReason Recorded = new(LeftoverSignal.RecordedAtInstall, FullScore, "created by the recorded install");

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RollbackPlan Plan(InstallLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var items = new List<LeftoverItem>();
        var reverts = new List<ValueRevert>();
        var kept = new List<string>();
        int gone = 0;

        List<string> folders = [];
        foreach (FileChange file in log.Files.Where(f => f.Change == ChangeKind.Created).OrderBy(f => f.Path.Length))
        {
            if (folders.Exists(f => ChangeReconciler.IsSameOrUnder(file.Path, f)))
            {
                continue;
            }

            FileState now = state.File(file.Path);
            if (now == FileState.Missing)
            {
                gone++;
                continue;
            }

            if (now == FileState.Directory)
            {
                folders.Add(file.Path);
            }

            items.Add(Item(now == FileState.Directory ? LeftoverKind.Folder : LeftoverKind.File, file.Path, null));
        }

        List<string> keys = [];
        foreach (KeyChange key in log.Keys.Where(k => k.Change == ChangeKind.Created).OrderBy(k => k.Path.Length))
        {
            if (keys.Exists(k => ChangeReconciler.IsSameOrUnder(key.Path, k)))
            {
                continue;
            }

            if (!state.KeyExists(key.Path))
            {
                gone++;
                continue;
            }

            keys.Add(key.Path);
            items.Add(Item(LeftoverKind.RegistryKey, key.Path, null));
        }

        foreach (ValueChange value in log.Values)
        {
            switch (value)
            {
                case { Change: ChangeKind.Created, RevertOnly: true }:
                    kept.Add($"{Describe(value)}: created by the install; a protected value is written back, never removed");
                    break;
                case { Change: ChangeKind.Created }:
                    if (state.Value(value.Key, value.Name) is null)
                    {
                        gone++;
                    }
                    else
                    {
                        items.Add(Item(LeftoverKind.RegistryValue, value.Key, value.Name));
                    }

                    break;
                case { PreviousKind: { } previousKind, PreviousData: { } previousData }:
                    RawRegistryValue? expected = value is { Kind: { } kind, Data: { } data } ? new RawRegistryValue(value.Name, kind, data) : null;
                    reverts.Add(new ValueRevert(value.Key, new RawRegistryValue(value.Name, previousKind, previousData), expected));
                    break;
                default:
                    kept.Add($"{Describe(value)}: {(value.Change == ChangeKind.Deleted ? "deleted" : "changed")} by the install; its earlier data was not recorded");
                    break;
            }
        }

        items.AddRange(log.FirewallRules.Select(r => Item(LeftoverKind.FirewallRule, r.Name, null)));
        kept.AddRange(log.Files.Where(f => f.Change == ChangeKind.Modified).Select(f => $"{f.Path}: changed by the install; its earlier content was not kept"));
        kept.AddRange(log.Files.Where(f => f.Change == ChangeKind.Deleted).Select(f => $"{f.Path}: deleted by the install"));
        kept.AddRange(log.Keys.Where(k => k.Change == ChangeKind.Deleted).Select(k => $"{k.Path}: deleted by the install"));

        return new RollbackPlan(log, items, reverts, kept, gone, Warnings(log))
        {
            Roots = [.. items.Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Path)],
        };
    }

    public async Task<CleanResult> ExecuteAsync(RollbackPlan plan, CleanOptions options, IProgress<CleanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        InstallLog log = plan.Log;
        var app = new InstalledApp
        {
            Id = log.AppId ?? $"log:{log.Id}",
            Sources = AppSources.None,
            Scope = log.UninstallKey?.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) == true ? InstallScope.User : InstallScope.Machine,
            DisplayName = log.Name,
            RegistryKey = log.UninstallKey,
        };

        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = log.FinishedAt ?? log.StartedAt,
            Roots = plan.Roots.Count > 0 ? plan.Roots : [.. plan.Items.Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Path)],
        };

        CleanResult result = await cleaner.CleanAsync(
            app,
            fingerprint,
            plan.Items,
            options with { Kind = TransactionKind.Rollback, Reverts = plan.Reverts },
            progress,
            cancellationToken).ConfigureAwait(false);

        logs.Save(log with { RollbackTransactionId = result.Transaction.Id, RolledBackAt = _time.GetUtcNow() });
        TrackerLog.RolledBack(logger, log.Id, result.Transaction.Id);
        return result;
    }

    private static List<string> Warnings(InstallLog log)
    {
        var warnings = new List<string>();
        if (log.RolledBackAt is { } rolledBack)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"This install was already rolled back on {rolledBack.ToLocalTime():yyyy-MM-dd HH:mm} (transaction {log.RollbackTransactionId})."));
        }

        if (log.UsesWindowsInstaller())
        {
            warnings.Add("Windows Installer did part of this install. Uninstall it first so Windows Installer removes its own records; a rollback leaves them.");
        }

        if (log.Statistics is { } stats && stats.EventsLost + stats.BuffersLost + stats.PendingDropped > 0)
        {
            warnings.Add("Events were lost while recording; the log may miss some of what the install created.");
        }

        return warnings;
    }

    private static LeftoverItem Item(LeftoverKind kind, string path, string? valueName) =>
        new(kind, path, valueName, FullScore, ConfidenceTier.Safe, [Recorded]);

    private static string Describe(ValueChange value) => $"{value.Key} -> \"{value.Name}\"";
}
