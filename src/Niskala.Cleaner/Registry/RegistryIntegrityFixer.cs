using Niskala.Core.Models;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cleaner.Registry;

public sealed record IntegrityFix(CleanResult Result, IReadOnlyList<IntegrityIssue> Fixed, IReadOnlyList<IntegrityIssue> NoLongerBroken);

public sealed class RegistryIntegrityFixer(RegistryIntegrityScanner scanner, LeftoverCleaner cleaner, TimeProvider? time = null)
{
    public const string SubjectId = "registry-integrity";
    public const string SubjectName = "Registry Integrity";
    private const int ItemScore = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IntegrityFix> FixAsync(
        IReadOnlyList<IntegrityIssue> issues,
        bool createRestorePoint,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var broken = new List<IntegrityIssue>();
        var settled = new List<IntegrityIssue>();
        foreach (IntegrityIssue issue in issues.Where(i => i.Fixable))
        {
            (scanner.IsStillBroken(issue) ? broken : settled).Add(issue);
        }

        var app = new InstalledApp
        {
            Id = SubjectId,
            Sources = AppSources.None,
            Scope = InstallScope.Machine,
            DisplayName = SubjectName,
        };
        var fingerprint = new Fingerprint
        {
            AppId = SubjectId,
            TakenAt = _time.GetUtcNow(),
            Roots = [.. broken.Where(i => i.ItemKind == LeftoverKind.FirewallRule)
                .Select(i => Path.GetDirectoryName(i.Target))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        LeftoverItem[] items = [.. broken.Select(i => new LeftoverItem(
            i.ItemKind,
            i.Path,
            i.ValueName,
            ItemScore,
            ConfidenceTier.Safe,
            [new LeftoverReason(LeftoverSignal.MissingProgram, ItemScore, i.Detail)]))];
        var options = new CleanOptions { Kind = TransactionKind.RegistryIntegrity, CreateRestorePoint = createRestorePoint };
        CleanResult result = await cleaner.CleanAsync(app, fingerprint, items, options, progress, cancellationToken).ConfigureAwait(false);
        return new IntegrityFix(result, broken, settled);
    }
}
