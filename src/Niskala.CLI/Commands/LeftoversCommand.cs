using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cli.Commands;

internal enum FingerprintSource
{
    Current,
    Stored,
}

internal sealed record LeftoversOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    InstalledApp App,
    FingerprintSource FingerprintSource,
    DateTimeOffset FingerprintTakenAt,
    LeftoverReport Report);

internal static class LeftoversCommand
{
    private const int SchemaVersion = 1;
    private const int MaxTreeDepth = 8;
    private const int MaxTreeLines = 200;

    public static Command Create(Option<bool> verbose)
    {
        var target = new Argument<string>("app") { Description = "Id or name of an installed application, or of one uninstalled with niskala uninstall." };
        var all = new Option<bool>("--all") { Description = "Also match hidden inventory entries." };
        var low = new Option<bool>("--low") { Description = "Also list low-confidence and excluded matches." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };
        var tree = new Option<int>("--tree")
        {
            Description = "Also list what each folder and key holds, this many levels deep: subfolders and files with size and date, subkeys and values with type and data.",
            DefaultValueFactory = _ => 0,
        };
        tree.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is < 0 or > MaxTreeDepth)
            {
                result.AddError($"--tree takes a depth from 0 to {MaxTreeDepth}.");
            }
        });

        var command = new Command("leftovers", "Find and score the files and registry entries an application left behind. Nothing is removed.")
        {
            target, all, low, json, tree,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string query = parse.GetValue(target)!;
            bool includeHidden = parse.GetValue(all);
            bool showLow = parse.GetValue(low);
            bool asJson = parse.GetValue(json);
            int depth = parse.GetValue(tree);
            return CommandRunner.RunAsync("leftovers", parse.GetValue(verbose), async (host, token) =>
            {
                IReadOnlyList<InstalledApp> installed = host.CreateInventoryCollector().Collect(cancellationToken: token).Apps;
                List<InstalledApp> matches = AppQuery.Find(installed.Where(a => includeHidden || a.IsVisible), query);
                InstalledApp app;
                Fingerprint fingerprint;
                FingerprintSource source;
                if (matches.Count == 1)
                {
                    app = matches[0];
                    fingerprint = host.CreateFingerprintCollector().Collect(app, cancellationToken: token);
                    source = FingerprintSource.Current;
                }
                else if (matches.Count == 0)
                {
                    IReadOnlyList<FingerprintRecord> records = host.CreateFingerprintStore().LoadAll();
                    List<InstalledApp> stored = AppQuery.Find(records.Select(r => r.App), query);
                    if (stored.Count != 1)
                    {
                        await AppQuery.ReportNoSingleMatch(query, stored, "No installed application or saved fingerprint has that name.").ConfigureAwait(false);
                        return ExitCodes.Usage;
                    }

                    app = stored[0];
                    fingerprint = records.First(r => ReferenceEquals(r.App, app)).Fingerprint;
                    source = FingerprintSource.Stored;
                }
                else
                {
                    await AppQuery.ReportNoSingleMatch(query, matches, string.Empty).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                LeftoverReport report = host.CreateLeftoverScanner().Scan(app, fingerprint, installed, token);
                if (asJson)
                {
                    var output = new LeftoversOutput(SchemaVersion, DateTimeOffset.Now, app, source, fingerprint.TakenAt, report);
                    Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.LeftoversOutput));
                }
                else
                {
                    WriteText(app, source, fingerprint, report, showLow, depth);
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteText(InstalledApp app, FingerprintSource source, Fingerprint fingerprint, LeftoverReport report, bool showLow, int depth)
    {
        TextWriter o = Console.Out;
        o.WriteLine($"{app.DisplayName ?? app.Id} {app.DisplayVersion}");
        o.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Fingerprint {(source == FingerprintSource.Current ? "taken now" : $"saved {fingerprint.TakenAt.ToLocalTime():yyyy-MM-dd HH:mm}")}; scan {report.Elapsed.TotalMilliseconds:0} ms"));
        foreach (string note in report.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        foreach (ConfidenceTier tier in (ConfidenceTier[])[ConfidenceTier.Safe, ConfidenceTier.Review, ConfidenceTier.Low])
        {
            List<LeftoverItem> items = [.. report.Items.Where(i => i.Tier == tier)];
            o.WriteLine();
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{tier} ({items.Count}{(items.Count == 0 ? string.Empty : $", {TextFormat.Bytes(items.Sum(i => i.Bytes ?? 0))}")})"));
            if (tier == ConfidenceTier.Low && !showLow)
            {
                if (items.Count > 0)
                {
                    o.WriteLine("  hidden, see --low");
                }

                continue;
            }

            foreach (LeftoverItem item in items)
            {
                string size = item.Kind switch
                {
                    LeftoverKind.Folder => string.Create(CultureInfo.InvariantCulture, $"  ({TextFormat.Bytes(item.Bytes)}, {item.Files:N0} files)"),
                    LeftoverKind.File => $"  ({TextFormat.Bytes(item.Bytes)})",
                    _ => string.Empty,
                };
                o.WriteLine($"  {item.Score,3}  {CleanText.Label(item.Kind),-6} {item.Path}{(item.ValueName is null ? string.Empty : $" -> \"{item.ValueName}\"")}{size}");
                foreach (LeftoverReason reason in item.Reasons)
                {
                    o.WriteLine($"         {reason.Weight,4:+0;-0}  {reason.Signal}: {reason.Detail}");
                }

                if (depth > 0)
                {
                    int lines = 0;
                    WriteTree(o, LeftoverTree.Root(item), 1, depth, ref lines);
                }
            }
        }

        o.WriteLine();
        o.WriteLine($"Excluded ({report.Excluded.Count})");
        if (showLow)
        {
            foreach (ExcludedLeftover excluded in report.Excluded)
            {
                o.WriteLine($"  {CleanText.Label(excluded.Kind),-6} {excluded.Path}{(excluded.ValueName is null ? string.Empty : $" -> \"{excluded.ValueName}\"")}  [{excluded.Reason}] {excluded.Detail}");
            }
        }
        else if (report.Excluded.Count > 0)
        {
            o.WriteLine("  hidden, see --low");
        }
    }

    private static void WriteTree(TextWriter o, LeftoverNode node, int level, int depth, ref int lines)
    {
        LeftoverChildren children = LeftoverTree.Children(node);
        string indent = new(' ', 9 + (level * 2));
        if (children.Error is { } error)
        {
            o.WriteLine($"{indent}(unreadable: {error})");
        }

        foreach (LeftoverNode child in children.Nodes)
        {
            if (++lines > MaxTreeLines)
            {
                o.WriteLine($"{indent}... more than {MaxTreeLines} entries");
                return;
            }

            o.WriteLine(indent + Describe(child));
            if (level < depth && child.HasChildren)
            {
                WriteTree(o, child, level + 1, depth, ref lines);
            }
        }

        if (children.Truncated)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{indent}... only the first {LeftoverTree.MaxChildren:N0} entries"));
        }
    }

    private static string Describe(LeftoverNode node)
    {
        string modified = node.Modified is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        return node.Kind switch
        {
            LeftoverKind.Folder when node.IsLink => $"{node.Name}\\  (link)  {modified}",
            LeftoverKind.Folder => string.Create(CultureInfo.InvariantCulture, $"{node.Name}\\  {TextFormat.Bytes(node.Bytes)}, {node.Files:N0} files  {modified}"),
            LeftoverKind.File => $"{node.Name}  {TextFormat.Bytes(node.Bytes)}  {modified}",
            LeftoverKind.RegistryKey => $"[{node.Name}]  {modified}",
            _ => $"{(node.Name.Length == 0 ? "(Default)" : node.Name)}  {node.ValueType}  {node.Data}",
        };
    }
}
