using System.Globalization;
using Niskala.Core.Commands;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Scanner.Leftovers;

public sealed class ScheduledTaskLeftoverProbe(ISystemTraceSource system)
{
    private const string WindowsFolder = @"\Microsoft\";

    internal void Find(ScanContext context, CandidateSet candidates, List<string> notes)
    {
        TaskList list = system.ScheduledTasks();
        if (list.Error != 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Scheduled tasks could not be read (0x{list.Error:X8})."));
            return;
        }

        if (list.UnreadableItems > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{list.UnreadableItems} scheduled tasks or task folders could not be read."));
        }

        var recorded = new HashSet<string>(context.Fingerprint.ScheduledTasks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, TaskTrace> task in list.Tasks.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (task.Key.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<TaskTrace> owned = [.. task.Where(a => context.Owner.OwnsPath(a.Command) || context.Owner.Mentions(a.Arguments))];
            bool orphaned = task.All(a => !ProgramExists(a.Command, context.Environment));
            if (owned.Count == 0 && !orphaned)
            {
                continue;
            }

            Evidence(context, candidates, task.Key, recorded.Contains(task.Key), owned, orphaned);
        }
    }

    public static bool StillLeftover(string path, string xml, OwnershipMatcher owner, ICommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(environment);
        return TaskXml.ReadExecActions(path, xml).All(a => owner.OwnsPath(a.Command) || owner.Mentions(a.Arguments) || !ProgramExists(a.Command, environment));
    }

    internal static bool ProgramExists(string command, ICommandEnvironment environment)
    {
        string path = environment.ExpandVariables(command.Trim().Trim('"'));
        return !Path.IsPathFullyQualified(path) || File.Exists(path);
    }

    private static void Evidence(ScanContext context, CandidateSet candidates, string path, bool recorded, List<TaskTrace> owned, bool orphaned)
    {
        string name = path[(path.LastIndexOf('\\') + 1)..];
        string[] folders = path[..Math.Max(path.LastIndexOf('\\'), 0)].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var evidence = new List<(LeftoverSignal Signal, string Detail)>();
        if (recorded)
        {
            evidence.Add((LeftoverSignal.InFingerprint, "task recorded before the uninstall"));
        }

        foreach (TaskTrace action in owned)
        {
            evidence.Add((LeftoverSignal.PointsToFingerprint, $"starts {action.Command}{(action.Arguments is { } arguments ? $" {arguments}" : string.Empty)}"));
        }

        NameMatch match = TokenNormalizer.MatchProduct(name, context.Tokens);
        if (match is { Kind: NameMatchKind.Exact, Token: { } token })
        {
            evidence.Add((LeftoverSignal.ExactName, $"task name is \"{token.Text}\""));
        }
        else if (match is { Kind: NameMatchKind.Partial, Token: { } part })
        {
            evidence.Add((LeftoverSignal.PartialName, $"task name holds \"{part.Text}\""));
        }

        foreach (string folder in folders)
        {
            if (TokenNormalizer.MatchProduct(folder, context.Tokens) is { Kind: NameMatchKind.Exact, Token: { } product })
            {
                evidence.Add((LeftoverSignal.ExactName, $"task folder is \"{product.Text}\""));
            }
            else if (match.Kind != NameMatchKind.None && TokenNormalizer.MatchesPublisher(folder, context.Tokens))
            {
                evidence.Add((LeftoverSignal.PublisherAndProduct, $"task {folder}\\{name}"));
            }
        }

        if (evidence.Count == 0)
        {
            return;
        }

        if (orphaned && owned.Count == 0)
        {
            evidence.Add((LeftoverSignal.MissingProgram, "the program it starts is gone"));
        }

        string? program = owned.Select(a => a.Command).FirstOrDefault(c => context.Owner.OwnsPath(c));
        foreach ((LeftoverSignal signal, string detail) in evidence)
        {
            candidates.Add(LeftoverKind.ScheduledTask, path, null, name, null, signal, detail, program);
        }
    }
}
