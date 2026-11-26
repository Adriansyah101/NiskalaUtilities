using System.Globalization;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Rollback;

namespace Niskala.Cli.Commands;

internal static class LogText
{
    private const int KeptShown = 10;

    public static void WriteSummary(TextWriter o, InstallLog log, string file)
    {
        o.WriteLine();
        o.WriteLine($"{log.Name}: {log.Installer}{(log.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" (exit code {code})") : string.Empty)}");
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Processes  {log.Processes.Count}{(log.Processes.Any(p => p.Adopted) ? ", with the Windows Installer service" : string.Empty)}"));
        o.WriteLine($"  Files      {Counts(log.Files.Select(f => f.Change))}");
        o.WriteLine($"  Keys       {Counts(log.Keys.Select(k => k.Change))}");
        o.WriteLine($"  Values     {Counts(log.Values.Select(v => v.Change))}");
        if (log.FirewallRules.Count > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Firewall   {log.FirewallRules.Count} rules"));
        }

        if (log.Filtered.Count > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Filtered   {log.Filtered.Values.Sum()} ({string.Join(", ", log.Filtered.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}"))})"));
        }

        o.WriteLine($"  App        {log.AppId ?? "no Uninstall entry was created"}");
        foreach (string note in log.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        o.WriteLine($"Log {log.Id}: {file}");
    }

    public static void WritePlan(TextWriter o, RollbackPlan plan, IReadOnlyList<string> emptyParents, bool permanent, bool restorePoint)
    {
        foreach (string warning in plan.Warnings)
        {
            o.WriteLine($"  ! {warning}");
        }

        CleanText.WritePlan(o, plan.Items, emptyParents, permanent, restorePoint);
        if (plan.Reverts.Count > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"To write back ({plan.Reverts.Count}):"));
            foreach (ValueRevert revert in plan.Reverts)
            {
                o.WriteLine($"  value  {revert.Key} -> \"{revert.Previous.Name}\"{(revert.Expected is null ? "  [deleted by the install]" : string.Empty)}");
            }
        }

        if (plan.Gone > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Already gone: {plan.Gone}"));
        }

        if (plan.Kept.Count > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Cannot be undone ({plan.Kept.Count}):"));
            foreach (string kept in plan.Kept.Take(KeptShown))
            {
                o.WriteLine($"  {kept}");
            }

            if (plan.Kept.Count > KeptShown)
            {
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  ... and {plan.Kept.Count - KeptShown} more: niskala logs show {plan.Log.Id}"));
            }
        }
    }

    public static bool IsEmpty(RollbackPlan plan) => plan.Items.Count == 0 && plan.Reverts.Count == 0;

    public static string Question(RollbackPlan plan) =>
        string.Create(CultureInfo.InvariantCulture, $"Undo the install of {plan.Log.Name}: remove {plan.Items.Count} items and write back {plan.Reverts.Count} values?");

    public static async Task<TransactionRecord> RollBackAsync(
        NiskalaHost host,
        LeftoverCleaner cleaner,
        RollbackPlan plan,
        CleanOptions options,
        bool assumeYes,
        TextWriter o,
        CancellationToken cancellationToken)
    {
        CleanResult result = await host.CreateLogRollback(cleaner).ExecuteAsync(plan, options, new CleanText.Progress(Console.Error), cancellationToken).ConfigureAwait(false);
        TransactionRecord transaction = await CleanText.OfferEmptyFoldersAsync(cleaner, result, assumeYes, o, cancellationToken).ConfigureAwait(false);
        CleanText.WriteTransaction(o, transaction);
        return transaction;
    }

    public static string Status(InstallLog log) => log.RolledBackAt is null ? string.Empty : "(rolled back)";

    private static string Counts(IEnumerable<ChangeKind> changes)
    {
        ChangeKind[] all = [.. changes];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{all.Count(c => c == ChangeKind.Created)} created, {all.Count(c => c == ChangeKind.Modified)} changed, {all.Count(c => c == ChangeKind.Deleted)} deleted");
    }
}
