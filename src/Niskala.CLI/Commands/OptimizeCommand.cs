using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Settings;
using Niskala.Interop.Processes;
using Niskala.Optimizer.Processes;
using Niskala.Optimizer.Resources;

namespace Niskala.Cli.Commands;

internal sealed record ResourceListOutput(int SchemaVersion, IReadOnlyList<ResourceItem> Items);

internal sealed record ResourceChangeOutput(int SchemaVersion, bool DryRun, ResourceItem Item, bool Enabled, TransactionRecord? Transaction);

internal sealed record ProcessListOutput(int SchemaVersion, IReadOnlyList<ProcessCandidate> Processes, IReadOnlyList<Restraint> Restraints);

internal sealed record RestraintOutput(int SchemaVersion, bool DryRun, IReadOnlyList<RestraintResult> Results);

internal static class OptimizeCommand
{
    private const int SchemaVersion = 1;
    private const int DefaultCapPercent = 20;
    private const int DefaultCapSeconds = 300;

    public static Command Create(Option<bool> verbose) =>
        new("optimize", "Resource Control for startup entries, services, scheduled tasks and context menus; Process Suspend for idle background programs.")
        {
            List(verbose), Switch(verbose, enable: false), Switch(verbose, enable: true), Processes(verbose),
            Restrain(verbose, "suspend", "Freeze a background process until niskala optimize resume.", RestraintKind.Suspended),
            Restrain(verbose, "eco", "Put a process in efficiency mode (EcoQoS and idle priority) until niskala optimize resume.", RestraintKind.Efficiency),
            Resume(verbose), Limit(verbose),
        };

    private static Command List(Option<bool> verbose)
    {
        var kinds = new Option<ResourceKind[]>("--kind") { Description = "Only these kinds: RunEntry, RunOnceEntry, StartupFolder, Service, ScheduledTask, ContextMenu.", AllowMultipleArgumentsPerToken = true };
        var json = new Option<bool>("--json") { Description = "Write the list as JSON to stdout." };
        var command = new Command("list", "List what starts with Windows or hooks into Explorer, with its estimated boot impact.") { kinds, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize list", parse.GetValue(verbose), (host, _) =>
        {
            IReadOnlyList<ResourceItem> items = host.CreateResourceControl().List(parse.GetValue(kinds) is { Length: > 0 } chosen ? chosen : null);
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new ResourceListOutput(SchemaVersion, items), CliJson.Output.ResourceListOutput));
                return Task.FromResult(ExitCodes.Success);
            }

            foreach (IGrouping<ResourceKind, ResourceItem> group in items.GroupBy(i => i.Kind))
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{group.Key} ({group.Count()})"));
                foreach (ResourceItem item in group)
                {
                    string state = item.Enabled ? "on " : "off";
                    string locked = item.CanToggle ? string.Empty : $"  [locked: {item.Blocked}]";
                    Console.Out.WriteLine($"  {state} {item.Impact,-7} {TextFormat.Pad(item.Name, 36)} {TextFormat.Pad(item.Publisher, 22)} {item.Id}{locked}");
                }
            }

            return Task.FromResult(ExitCodes.Success);
        }, cancellationToken));
        return command;
    }

    private static Command Switch(Option<bool> verbose, bool enable)
    {
        string name = enable ? "enable" : "disable";
        var target = new Argument<string>("item") { Description = "The id niskala optimize list shows, or a unique name." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change; change nothing." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var json = new Option<bool>("--json") { Description = "Write the item and the transaction as JSON to stdout." };
        var command = new Command(name, enable
            ? "Switch an item back on; services get their earlier start type from the journal."
            : "Switch an item off; the earlier state goes into the journal and niskala restore puts it back.")
        {
            target, dryRun, yes, json,
        };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize " + name, parse.GetValue(verbose), async (host, token) =>
        {
            ResourceControl control = host.CreateResourceControl();
            string query = parse.GetValue(target)!;
            List<ResourceItem> matches = Find(control.List(), query);
            if (matches.Count != 1)
            {
                await Console.Error.WriteLineAsync(matches.Count == 0
                    ? $"Nothing matches \"{query}\"; niskala optimize list shows the ids."
                    : $"\"{query}\" matches {matches.Count} items; use the id: {string.Join(", ", matches.Select(m => m.Id))}").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            ResourceItem item = matches[0];
            bool asJson = parse.GetValue(json);
            TextWriter o = asJson ? Console.Error : Console.Out;
            if (!item.CanToggle)
            {
                await Console.Error.WriteLineAsync($"{item.Name} is protected: {item.Blocked}").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            if (item.Enabled == enable)
            {
                o.WriteLine($"{item.Name} is already {(enable ? "on" : "off")}.");
                return ExitCodes.Success;
            }

            o.WriteLine($"{(enable ? "Switch on" : "Switch off")}: {item.Name} ({item.Kind}, {item.Location})");
            if (parse.GetValue(dryRun))
            {
                o.WriteLine("Dry run: nothing was changed.");
                WriteJson(asJson, new ResourceChangeOutput(SchemaVersion, true, item, enable, null));
                return ExitCodes.Success;
            }

            if (await Confirmation.RequireAsync($"{(enable ? "Switch on" : "Switch off")} {item.Name}?", parse.GetValue(yes), token).ConfigureAwait(false) is { } declined)
            {
                return declined;
            }

            ResourceChange change = control.SetEnabled(item, enable)!;
            SettingChange setting = change.Setting;
            o.WriteLine(setting.Status switch
            {
                SettingStatus.Applied => $"Done. Transaction {change.Transaction.Id}: niskala restore {change.Transaction.Id} undoes it.",
                SettingStatus.Unchanged => "It was already that way.",
                _ => $"Not changed: {setting.Detail}",
            });
            if (change.Succeeded && item.Kind == ResourceKind.ContextMenu)
            {
                o.WriteLine("Explorer picks this up after it restarts or you sign in again.");
            }

            WriteJson(asJson, new ResourceChangeOutput(SchemaVersion, false, item, enable, change.Transaction));
            return change.Succeeded ? ExitCodes.Success
                : setting.Detail?.Contains("elevation", StringComparison.Ordinal) == true ? ExitCodes.ElevationRequired
                : ExitCodes.Failure;
        }, cancellationToken));
        return command;
    }

    private static Command Processes(Option<bool> verbose)
    {
        var idle = new Option<bool>("--idle") { Description = "Only idle background processes that can be restrained." };
        var sample = new Option<double>("--sample") { Description = "Seconds to measure CPU use over.", DefaultValueFactory = _ => 2 };
        var json = new Option<bool>("--json") { Description = "Write the list as JSON to stdout." };
        var command = new Command("processes", "List processes of this session with their CPU use and whether Process Suspend may touch them.") { idle, sample, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize processes", parse.GetValue(verbose), async (host, token) =>
        {
            ProcessRestrainer restrainer = host.CreateProcessRestrainer();
            IReadOnlyList<ProcessCandidate> all = await restrainer.ListAsync(TimeSpan.FromSeconds(Math.Clamp(parse.GetValue(sample), 0, 30)), token).ConfigureAwait(false);
            IReadOnlyList<ProcessCandidate> shown = parse.GetValue(idle) ? [.. all.Where(c => c.CanRestrain && c.IsIdleBackground)] : all;
            if (parse.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new ProcessListOutput(SchemaVersion, shown, restrainer.Current()), CliJson.Output.ProcessListOutput));
                return ExitCodes.Success;
            }

            foreach (ProcessCandidate candidate in shown)
            {
                string flags = candidate.CanRestrain ? (candidate.IsIdleBackground ? "idle background" : candidate.HasWindow ? "has a window" : "busy") : $"refused: {candidate.Refusal}";
                string held = candidate.Restraint is { } kind ? $" [{kind}]" : string.Empty;
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{candidate.Id,7} {TextFormat.Pad(candidate.Name, 30)} {candidate.CpuPercent,6:0.0}%  {flags}{held}"));
            }

            return ExitCodes.Success;
        }, cancellationToken));
        return command;
    }

    private static Command Restrain(Option<bool> verbose, string name, string description, RestraintKind kind)
    {
        var processId = new Argument<int>("pid") { Description = "Process id, as niskala optimize processes shows it." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Only check whether the process may be restrained." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };
        var command = new Command(name, description) { processId, dryRun, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize " + name, parse.GetValue(verbose), (host, _) =>
        {
            int id = parse.GetValue(processId);
            bool isDryRun = parse.GetValue(dryRun);
            ProcessRestrainer restrainer = host.CreateProcessRestrainer();
            RestraintResult result = isDryRun
                ? new RestraintResult(id, RestraintOutcome.NotRestrained, new ProcessGuard(SystemProcesses.Instance).Check(id))
                : kind == RestraintKind.Suspended ? restrainer.Suspend(id) : restrainer.SetEfficiency(id);
            return Task.FromResult(Report(parse.GetValue(json), isDryRun, [result]));
        }, cancellationToken));
        return command;
    }

    private static Command Resume(Option<bool> verbose)
    {
        var processId = new Argument<int?>("pid") { Description = "Process id; leave out with --all.", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all") { Description = "Resume every process niskala restrained." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };
        var command = new Command("resume", "Resume a suspended process or take it out of efficiency mode; only processes niskala restrained.") { processId, all, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize resume", parse.GetValue(verbose), async (host, _) =>
        {
            ProcessRestrainer restrainer = host.CreateProcessRestrainer();
            if (parse.GetValue(all))
            {
                return Report(parse.GetValue(json), false, restrainer.UndoAll());
            }

            if (parse.GetValue(processId) is not { } id)
            {
                await Console.Error.WriteLineAsync("Give a process id or --all.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            return Report(parse.GetValue(json), false, [restrainer.Undo(id)]);
        }, cancellationToken));
        return command;
    }

    private static Command Limit(Option<bool> verbose)
    {
        var processId = new Argument<int>("pid") { Description = "Process id, as niskala optimize processes shows it." };
        var cpu = new Option<int>("--cpu") { Description = "CPU share in percent of the whole machine (1-100).", DefaultValueFactory = _ => DefaultCapPercent };
        var seconds = new Option<int>("--seconds") { Description = "How long to hold the cap; Ctrl+C lifts it sooner.", DefaultValueFactory = _ => DefaultCapSeconds };
        var dryRun = new Option<bool>("--dry-run") { Description = "Only check whether the process may be capped." };
        var command = new Command("limit", "Cap a process's CPU share with a job object while this command runs; the cap lifts when it ends.") { processId, cpu, seconds, dryRun };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("optimize limit", parse.GetValue(verbose), async (host, token) =>
        {
            int id = parse.GetValue(processId);
            int percent = Math.Clamp(parse.GetValue(cpu), 1, 100);
            if (parse.GetValue(dryRun))
            {
                return Report(false, true, [new RestraintResult(id, RestraintOutcome.NotRestrained, new ProcessGuard(SystemProcesses.Instance).Check(id))]);
            }

            RestraintResult result = host.CreateProcessRestrainer().Cap(id, percent, out CpuCapJob? job);
            if (job is null)
            {
                return Report(false, false, [result]);
            }

            using (job)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Process {id} capped at {percent}% CPU; press Ctrl+C to lift the cap."));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, parse.GetValue(seconds))), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.Out.WriteLine("Cap lifted.");
                    return ExitCodes.Success;
                }
            }

            Console.Out.WriteLine("Cap lifted.");
            return ExitCodes.Success;
        }, cancellationToken));
        return command;
    }

    private static int Report(bool asJson, bool dryRun, IReadOnlyList<RestraintResult> results)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new RestraintOutput(SchemaVersion, dryRun, results), CliJson.Output.RestraintOutput));
        }
        else
        {
            foreach (RestraintResult result in results)
            {
                Console.Out.WriteLine(result switch
                {
                    { Outcome: RestraintOutcome.NotRestrained } when dryRun => result.Refusal == ProcessRefusal.None
                        ? string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} may be restrained. Dry run: nothing was changed.")
                        : string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} is off limits: {result.Refusal}."),
                    { Outcome: RestraintOutcome.Refused } => string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} is off limits: {result.Refusal}."),
                    { Outcome: RestraintOutcome.Failed } => string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId}: Win32 error {result.Error}."),
                    { Outcome: RestraintOutcome.NotRestrained } => string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} was not restrained by niskala."),
                    { Outcome: RestraintOutcome.Undone } => string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} runs normally again."),
                    _ => string.Create(CultureInfo.InvariantCulture, $"Process {result.ProcessId} restrained; niskala optimize resume {result.ProcessId} undoes it."),
                });
            }
        }

        return results.Any(r => r.Outcome is RestraintOutcome.Failed or RestraintOutcome.Refused) || (dryRun && results.Any(r => r.Refusal != ProcessRefusal.None))
            ? ExitCodes.Failure
            : ExitCodes.Success;
    }

    private static List<ResourceItem> Find(IReadOnlyList<ResourceItem> items, string query)
    {
        List<ResourceItem> exact = [.. items.Where(i => i.Id.Equals(query, StringComparison.OrdinalIgnoreCase))];
        return exact.Count > 0 ? exact : [.. items.Where(i => i.Name.Equals(query, StringComparison.CurrentCultureIgnoreCase))];
    }

    private static void WriteJson(bool asJson, ResourceChangeOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.ResourceChangeOutput));
        }
    }
}
