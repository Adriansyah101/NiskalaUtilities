using System.Globalization;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cli.Commands;

internal static class CleanText
{
    public static IReadOnlyList<LeftoverItem> Selected(LeftoverReport report, bool includeReview) =>
        [.. report.Items.Where(i => i.Tier == ConfidenceTier.Safe || (includeReview && i.Tier == ConfidenceTier.Review))];

    public static void WritePlan(TextWriter o, IReadOnlyList<LeftoverItem> items, IReadOnlyList<string> emptyParents, bool permanent, bool restorePoint)
    {
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"To remove ({items.Count}, {TextFormat.Bytes(items.Sum(i => i.Bytes ?? 0))}):"));
        foreach (LeftoverItem item in items)
        {
            o.WriteLine($"  {item.Score,3}  {Label(item.Kind),-6} {Describe(item.Kind, item.Path, item.ValueName)}");
        }

        foreach (string folder in emptyParents)
        {
            o.WriteLine($"  then offered: empty folder {folder}");
        }

        o.WriteLine(permanent ? "  Files: deleted permanently; in-use files at the next start" : "  Files: moved to the Recycle Bin");
        o.WriteLine(restorePoint
            ? $"  Restore point: {(Environment.IsPrivilegedProcess ? "yes" : "no, not elevated")}"
            : "  Restore point: no (--no-restore-point)");
        if (!Environment.IsPrivilegedProcess)
        {
            o.WriteLine("  Not elevated: machine-wide keys, services, firewall rules and Program Files will fail.");
        }
    }

    public static void WriteTransaction(TextWriter o, TransactionRecord transaction)
    {
        o.WriteLine();
        if (transaction.RestorePoint is { } point)
        {
            o.WriteLine(point.Created
                ? string.Create(CultureInfo.InvariantCulture, $"Restore point #{point.SequenceNumber} created.")
                : $"Restore point: {point.Note}");
        }

        foreach (RemovalStatus status in (RemovalStatus[])[RemovalStatus.Removed, RemovalStatus.Reverted, RemovalStatus.ScheduledAtReboot, RemovalStatus.Skipped, RemovalStatus.Failed])
        {
            List<RemovalRecord> records = [.. transaction.Items.Where(i => i.Status == status)];
            if (records.Count == 0)
            {
                continue;
            }

            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Title(status)} ({records.Count})"));
            foreach (RemovalRecord record in records)
            {
                o.WriteLine($"  {Label(record.Kind),-6} {Describe(record.Kind, record.Path, record.ValueName)}{(record.Detail is null ? string.Empty : $"  [{record.Detail}]")}");
            }
        }

        o.WriteLine($"Transaction {transaction.Id}: niskala restore {transaction.Id}");
    }

    public static async Task<TransactionRecord> OfferEmptyFoldersAsync(LeftoverCleaner cleaner, CleanResult result, bool assumeYes, TextWriter o, CancellationToken cancellationToken)
    {
        if (result.EmptyParents.Count == 0)
        {
            return result.Transaction;
        }

        o.WriteLine();
        o.WriteLine("These folders are empty now:");
        foreach (string folder in result.EmptyParents)
        {
            o.WriteLine($"  {folder}");
        }

        if (await Confirmation.RequireAsync("Remove them too?", assumeYes, cancellationToken).ConfigureAwait(false) is not null)
        {
            return result.Transaction;
        }

        TransactionRecord updated = cleaner.RemoveEmptyFolders(result.Transaction.Id, result.EmptyParents);
        foreach (RemovalRecord record in updated.Items.Where(i => i.EmptyFolder))
        {
            o.WriteLine($"  {record.Status}: {record.Path}{(record.Detail is null ? string.Empty : $"  [{record.Detail}]")}");
        }

        return updated;
    }

    public static int ExitCode(TransactionRecord transaction) =>
        transaction.Items.Any(i => i.Status == RemovalStatus.Failed) ? ExitCodes.Failure : ExitCodes.Success;

    public static string Label(LeftoverKind kind) => kind switch
    {
        LeftoverKind.Folder => "folder",
        LeftoverKind.File => "file",
        LeftoverKind.RegistryKey => "key",
        LeftoverKind.FirewallRule => "rule",
        LeftoverKind.ScheduledTask => "task",
        _ => "value",
    };

    public static string Describe(LeftoverKind kind, string path, string? valueName) => kind switch
    {
        LeftoverKind.RegistryValue => $"{path} -> \"{valueName}\"",
        LeftoverKind.FirewallRule => $"\"{path}\"",
        _ => path,
    };

    private static string Title(RemovalStatus status) => status switch
    {
        RemovalStatus.Removed => "Removed",
        RemovalStatus.Reverted => "Written back",
        RemovalStatus.ScheduledAtReboot => "At the next start",
        RemovalStatus.Skipped => "Skipped",
        _ => "Failed",
    };

    internal sealed class Progress(TextWriter writer) : IProgress<CleanProgress>
    {
        public void Report(CleanProgress value)
        {
            string? line = value switch
            {
                { Stage: CleanStage.RestorePoint } => "Creating a restore point...",
                { Stage: CleanStage.Backup } => "Backing up registry keys and firewall rules...",
                { Item: { } item } => $"  {item.Status,-17} {Label(item.Kind),-6} {Describe(item.Kind, item.Path, item.ValueName)}",
                _ => null,
            };

            if (line is not null)
            {
                writer.WriteLine(line);
            }
        }
    }
}
