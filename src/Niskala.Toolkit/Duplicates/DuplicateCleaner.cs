using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Toolkit.Duplicates;

public sealed record DuplicateRemoval(CleanResult? Result, IReadOnlyList<DuplicateFile> Removed, IReadOnlyList<DuplicateFile> Changed);

public sealed class DuplicateCleaner(LeftoverCleaner cleaner, TimeProvider? time = null)
{
    public const string SubjectId = "duplicate-scan";
    public const string SubjectName = "Duplicate Scan";
    private const int ItemScore = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static IReadOnlyList<DuplicateFile> Plan(IReadOnlyList<DuplicateGroup> groups, KeepRule rule)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return [.. groups.SelectMany(g => DuplicateSelection.Removable(g, rule))];
    }

    public async Task<DuplicateRemoval> RemoveAsync(
        IReadOnlyList<DuplicateGroup> groups,
        KeepRule rule,
        bool permanent,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var verified = new List<DuplicateFile>();
        var changed = new List<DuplicateFile>();
        foreach (DuplicateGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DuplicateFile keeper = DuplicateSelection.Keeper(group, rule);
            bool keeperIntact = Same(keeper, group);
            foreach (DuplicateFile file in DuplicateSelection.Removable(group, rule))
            {
                (keeperIntact && Same(file, group) ? verified : changed).Add(file);
            }
        }

        if (verified.Count == 0)
        {
            return new DuplicateRemoval(null, [], changed);
        }

        var app = new InstalledApp { Id = SubjectId, Sources = AppSources.None, Scope = InstallScope.User, DisplayName = SubjectName };
        var fingerprint = new Fingerprint { AppId = SubjectId, TakenAt = _time.GetUtcNow() };
        LeftoverItem[] items = [.. verified.Select(f => new LeftoverItem(LeftoverKind.File, f.Path, null, ItemScore, ConfidenceTier.Safe, []) { Bytes = f.Bytes, Files = 1 })];
        var options = new CleanOptions { Kind = TransactionKind.DuplicateClean, Permanent = permanent, CreateRestorePoint = false };
        CleanResult result = await cleaner.CleanAsync(app, fingerprint, items, options, progress, cancellationToken).ConfigureAwait(false);
        return new DuplicateRemoval(result, verified, changed);
    }

    private static bool Same(DuplicateFile file, DuplicateGroup group)
    {
        try
        {
            var info = new FileInfo(file.Path);
            return info.Exists && info.Length == group.Bytes && DuplicateFinder.FullHash(file.Path) == group.Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
