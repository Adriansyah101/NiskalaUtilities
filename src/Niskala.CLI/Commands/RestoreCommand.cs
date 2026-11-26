using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Host;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cli.Commands;

internal sealed record RestoreListOutput(int SchemaVersion, IReadOnlyList<TransactionRecord> Transactions);

internal sealed record RestoreOutput(int SchemaVersion, RestoreReport Report);

internal static class RestoreCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose)
    {
        var id = new Argument<string?>("transaction") { Description = "Transaction id; omit to list them.", Arity = ArgumentArity.ZeroOrOne };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var json = new Option<bool>("--json") { Description = "Write the list or the restore report as JSON to stdout." };

        var command = new Command("restore", "List Guardian transactions, or restore the registry backup, Recycle Bin items, firewall rules and scheduled tasks of one.")
        {
            id, yes, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string? transactionId = parse.GetValue(id);
            bool assumeYes = parse.GetValue(yes);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("restore", parse.GetValue(verbose), async (host, token) =>
            {
                TransactionStore store = host.CreateTransactionStore();
                if (transactionId is null)
                {
                    IReadOnlyList<TransactionRecord> all = store.List();
                    if (asJson)
                    {
                        Console.Out.WriteLine(JsonSerializer.Serialize(new RestoreListOutput(SchemaVersion, all), CliJson.Output.RestoreListOutput));
                    }
                    else
                    {
                        WriteList(all);
                    }

                    return ExitCodes.Success;
                }

                TransactionRecord? transaction = store.Load(transactionId);
                if (transaction is null)
                {
                    await Console.Error.WriteLineAsync($"No transaction {transactionId}. Run niskala restore to list them.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                TextWriter o = asJson ? Console.Error : Console.Out;
                WritePlan(o, transaction);
                if (transaction.RestoredAt is not null)
                {
                    return ExitCodes.Usage;
                }

                if (await Confirmation.RequireAsync("Restore it?", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                RestoreReport report = host.CreateTransactionRestorer().Restore(transactionId)!;
                WriteReport(o, report);
                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new RestoreOutput(SchemaVersion, report), CliJson.Output.RestoreOutput));
                }

                return report.Problems.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteList(IReadOnlyList<TransactionRecord> all)
    {
        TextWriter o = Console.Out;
        if (all.Count == 0)
        {
            o.WriteLine("No transactions yet.");
            return;
        }

        o.WriteLine($"{"Id",-22} {"When",-16} {"Kind",-9} {"Items",5}  Application");
        foreach (TransactionRecord t in all)
        {
            int removed = t.Items.Count(i => i.Status is RemovalStatus.Removed or RemovalStatus.ScheduledAtReboot);
            string state = t switch
            {
                { RestoredAt: not null } => "  (restored)",
                { FinishedAt: null, Kind: not TransactionKind.Uninstall } => "  (unfinished)",
                _ => string.Empty,
            };
            o.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{t.Id,-22} {t.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} {t.Kind,-9} {removed,5}  {TextFormat.Pad(t.AppName ?? t.AppId, 40)}{state}"));
        }
    }

    private static void WritePlan(TextWriter o, TransactionRecord t)
    {
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{t.Kind} of {t.AppName ?? t.AppId}, {t.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        int recycled = t.Items.Count(i => i.Status == RemovalStatus.Removed && i.RecycledPath is not null);
        int folders = t.Items.Count(i => i.Status == RemovalStatus.Removed && i.EmptyFolder);
        int rules = t.Items.Count(i => i.Status == RemovalStatus.Removed && i.Kind == LeftoverKind.FirewallRule);
        int tasks = t.Items.Count(i => i.Status == RemovalStatus.Removed && i.Kind == LeftoverKind.ScheduledTask);
        int gone = t.Items.Count(i => i is { Status: RemovalStatus.Removed or RemovalStatus.ScheduledAtReboot, RecycledPath: null, EmptyFolder: false }
            && i.Kind is LeftoverKind.File or LeftoverKind.Folder);
        o.WriteLine($"  Registry backup  {(t.HasRegistryBackup ? "yes" : "none")}");
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Recycle Bin      {recycled} items"));
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Empty folders    {folders}"));
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Firewall rules   {rules}"));
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Scheduled tasks  {tasks}"));
        if (gone > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Not restorable   {gone} deleted permanently"));
        }

        if (t.Kind == TransactionKind.Uninstall)
        {
            o.WriteLine("  Only registry entries come back; the program files need the restore point.");
        }

        if (t.RestorePoint is { Created: true, SequenceNumber: { } sequence })
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Restore point    #{sequence} (rstrui.exe)"));
        }

        if (t.RestoredAt is { } restoredAt)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Already restored {restoredAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        }
    }

    private static void WriteReport(TextWriter o, RestoreReport report)
    {
        o.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Restored: {report.ValuesWritten} registry values ({report.ValuesKept} kept as they are), {report.ItemsRestored} items, {report.RulesRestored} firewall rules."));
        foreach (string problem in report.Problems)
        {
            o.WriteLine($"  ! {problem}");
        }

        foreach (string note in report.Notes)
        {
            o.WriteLine($"  note: {note}");
        }
    }
}
