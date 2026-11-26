using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Rollback;

namespace Niskala.Cli.Commands;

internal sealed record LogsListOutput(int SchemaVersion, DateTimeOffset GeneratedAt, IReadOnlyList<InstallLog> Logs);

internal sealed record RollbackOutput(int SchemaVersion, DateTimeOffset GeneratedAt, bool DryRun, RollbackPlan Plan, TransactionRecord? Transaction);

internal static class LogsCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write JSON to stdout." };
        var command = new Command("logs", "List the installs niskala track recorded.")
        {
            json, Show(verbose), Rollback(verbose), Delete(verbose),
        };

        command.SetAction((parse, cancellationToken) =>
        {
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("logs", parse.GetValue(verbose), (host, _) =>
            {
                IReadOnlyList<InstallLog> logs = host.CreateInstallLogStore().List();
                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new LogsListOutput(SchemaVersion, DateTimeOffset.Now, logs), CliJson.Output.LogsListOutput));
                    return Task.FromResult(ExitCodes.Success);
                }

                if (logs.Count == 0)
                {
                    Console.Out.WriteLine("No install logs yet. Record one with niskala track <installer>.");
                    return Task.FromResult(ExitCodes.Success);
                }

                foreach (InstallLog log in logs)
                {
                    Console.Out.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{log.Id}  {log.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {TextFormat.Pad(log.Name, 32)}  {log.Files.Count,5} files {log.Keys.Count,4} keys {log.Values.Count,4} values  {LogText.Status(log)}"));
                }

                return Task.FromResult(ExitCodes.Success);
            }, cancellationToken);
        });

        return command;
    }

    private static Command Show(Option<bool> verbose)
    {
        var id = new Argument<string>("id") { Description = "Log id, as niskala logs lists it." };
        var json = new Option<bool>("--json") { Description = "Write the log as JSON to stdout." };
        var command = new Command("show", "Show everything an install log recorded.") { id, json };
        command.SetAction((parse, cancellationToken) =>
        {
            string logId = parse.GetValue(id)!;
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("logs show", parse.GetValue(verbose), async (host, _) =>
            {
                if (await LoadAsync(host, logId).ConfigureAwait(false) is not { } log)
                {
                    return ExitCodes.Usage;
                }

                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(log, CliJson.Output.InstallLog));
                    return ExitCodes.Success;
                }

                TextWriter o = Console.Out;
                LogText.WriteSummary(o, log, host.CreateInstallLogStore().PathOf(log.Id));
                o.WriteLine();
                foreach (RecordedProcess process in log.Processes)
                {
                    o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"process {process.Id,6} <- {process.ParentId,-6} {process.ImagePath}{(process.Adopted ? "  [adopted]" : string.Empty)}"));
                }

                foreach (FileChange file in log.Files)
                {
                    o.WriteLine($"{Change(file.Change)} {(file.IsDirectory ? "folder" : "file"),-6} {file.Path}");
                }

                foreach (KeyChange key in log.Keys)
                {
                    o.WriteLine($"{Change(key.Change)} key    {key.Path}{(key.Origin is null ? string.Empty : $"  [{key.Origin}]")}");
                }

                foreach (ValueChange value in log.Values)
                {
                    o.WriteLine($"{Change(value.Change)} value  {value.Key} -> \"{value.Name}\" = {Data(value.Kind, value.Data)}{(value.PreviousKind is null ? string.Empty : $"  (was {Data(value.PreviousKind, value.PreviousData)})")}");
                }

                foreach (FirewallRuleChange rule in log.FirewallRules)
                {
                    o.WriteLine($"+ rule   \"{rule.Name}\"  {rule.ApplicationName}");
                }

                if (log.RolledBackAt is { } rolledBack)
                {
                    o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Rolled back on {rolledBack.ToLocalTime():yyyy-MM-dd HH:mm}: niskala restore {log.RollbackTransactionId}"));
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static Command Rollback(Option<bool> verbose)
    {
        var id = new Argument<string>("id") { Description = "Log id, as niskala logs lists it." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation; empty folders are removed as well." };
        var permanent = new Option<bool>("--permanent") { Description = "Delete files permanently instead of moving them to the Recycle Bin." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be undone; change nothing." };
        var noRestorePoint = new Option<bool>("--no-restore-point") { Description = "Skip the System Restore point; the registry backup is still made." };
        var json = new Option<bool>("--json") { Description = "Write the plan and the transaction as JSON to stdout; progress goes to stderr." };
        var command = new Command("rollback", "Undo a recorded install exactly, without its uninstaller, as one transaction niskala restore can undo.")
        {
            id, yes, permanent, dryRun, noRestorePoint, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string logId = parse.GetValue(id)!;
            bool assumeYes = parse.GetValue(yes);
            bool isPermanent = parse.GetValue(permanent);
            bool isDryRun = parse.GetValue(dryRun);
            bool restorePoint = !parse.GetValue(noRestorePoint);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("logs rollback", parse.GetValue(verbose), async (host, token) =>
            {
                if (await LoadAsync(host, logId).ConfigureAwait(false) is not { } log)
                {
                    return ExitCodes.Usage;
                }

                LeftoverCleaner cleaner = host.CreateLeftoverCleaner();
                RollbackPlan plan = host.CreateLogRollback(cleaner).Plan(log);
                IReadOnlyList<string> predicted = cleaner.PredictEmptyParents(plan.Items);
                TextWriter o = asJson ? Console.Error : Console.Out;
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{log.Name}, recorded {log.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
                LogText.WritePlan(o, plan, predicted, isPermanent, restorePoint);
                if (isDryRun || LogText.IsEmpty(plan))
                {
                    o.WriteLine(LogText.IsEmpty(plan) ? "Nothing to undo." : "Dry run: nothing was changed.");
                    WriteJson(asJson, new RollbackOutput(SchemaVersion, DateTimeOffset.Now, isDryRun, plan, null));
                    return ExitCodes.Success;
                }

                if (await Confirmation.RequireAsync(LogText.Question(plan), assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                var options = new CleanOptions { Permanent = isPermanent, CreateRestorePoint = restorePoint };
                TransactionRecord transaction = await LogText.RollBackAsync(host, cleaner, plan, options, assumeYes, o, token).ConfigureAwait(false);
                WriteJson(asJson, new RollbackOutput(SchemaVersion, DateTimeOffset.Now, false, plan, transaction));
                return CleanText.ExitCode(transaction);
            }, cancellationToken);
        });

        return command;
    }

    private static Command Delete(Option<bool> verbose)
    {
        var id = new Argument<string>("id") { Description = "Log id, as niskala logs lists it." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var command = new Command("delete", "Delete an install log. What the install changed stays as it is.") { id, yes };
        command.SetAction((parse, cancellationToken) =>
        {
            string logId = parse.GetValue(id)!;
            bool assumeYes = parse.GetValue(yes);
            return CommandRunner.RunAsync("logs delete", parse.GetValue(verbose), async (host, token) =>
            {
                if (await LoadAsync(host, logId).ConfigureAwait(false) is not { } log)
                {
                    return ExitCodes.Usage;
                }

                if (await Confirmation.RequireAsync($"Delete the install log of {log.Name}? It can no longer be rolled back.", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                host.CreateInstallLogStore().Delete(log.Id);
                Console.Out.WriteLine($"Deleted log {log.Id}.");
                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static async Task<InstallLog?> LoadAsync(NiskalaHost host, string id)
    {
        try
        {
            if (host.CreateInstallLogStore().Load(id) is { } log)
            {
                return log;
            }

            await Console.Error.WriteLineAsync($"No install log {id}. niskala logs lists them.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            await Console.Error.WriteLineAsync($"{id}: {ex.Message}").ConfigureAwait(false);
        }

        return null;
    }

    private static void WriteJson(bool asJson, RollbackOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.RollbackOutput));
        }
    }

    private static char Change(ChangeKind change) => change switch
    {
        ChangeKind.Created => '+',
        ChangeKind.Modified => '~',
        _ => '-',
    };

    private static string Data(int? kind, byte[]? data) => (kind, data) switch
    {
        (_, null) => "-",
        (1 or 2 or 7, _) => $"\"{Encoding.Unicode.GetString(data).TrimEnd('\0').Replace('\0', '|')}\"",
        (4, { Length: 4 }) => BitConverter.ToInt32(data).ToString(CultureInfo.InvariantCulture),
        _ => data.Length <= 32 ? Convert.ToHexString(data) : string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexString(data.AsSpan(0, 32))}... ({data.Length} bytes)"),
    };
}
