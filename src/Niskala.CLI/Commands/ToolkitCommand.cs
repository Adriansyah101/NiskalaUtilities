using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Toolkit.Duplicates;
using Niskala.Toolkit.Storage;
using Niskala.Toolkit.Wipe;

namespace Niskala.Cli.Commands;

internal sealed record StorageMapOutput(int SchemaVersion, StorageNode Root, IReadOnlyList<ExtensionGroup> Extensions, int Inaccessible, int Links, long ElapsedMs);

internal sealed record DuplicateOutput(int SchemaVersion, bool DryRun, KeepRule Keep, DuplicateReport Report, IReadOnlyList<DuplicateFile> Planned, DuplicateRemoval? Removal);

internal sealed record WipeOutput(int SchemaVersion, bool DryRun, WipeMethod Method, IReadOnlyList<WipeResult> Files, FreeSpaceResult? FreeSpace);

internal static class ToolkitCommand
{
    private const int SchemaVersion = 1;
    private const int DefaultDepth = 2;
    private const int DefaultTop = 15;

    public static Command Create(Option<bool> verbose) =>
        new("toolkit", "Storage Map, Duplicate Scan and Secure Wipe.")
        {
            Map(verbose), Duplicates(verbose), Wipe(verbose), WipeFree(verbose),
        };

    private static Command Map(Option<bool> verbose)
    {
        var folder = new Argument<string>("folder") { Description = "Folder or drive to map, such as C:\\ or D:\\Media." };
        var depth = new Option<int>("--depth") { Description = "Levels of sub-folders to show.", DefaultValueFactory = _ => DefaultDepth };
        var top = new Option<int>("--top") { Description = "Largest sub-folders and extensions to show per level.", DefaultValueFactory = _ => DefaultTop };
        var json = new Option<bool>("--json") { Description = "Write the map, cut at --depth, as JSON to stdout." };
        var command = new Command("map", "Storage Map: folder sizes, file counts and extension groups; links and junctions are counted, never followed.") { folder, depth, top, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("toolkit map", parse.GetValue(verbose), async (_, token) =>
        {
            int levels = Math.Clamp(parse.GetValue(depth), 0, 32);
            int count = Math.Clamp(parse.GetValue(top), 1, 1000);
            var progress = new MapProgress(Console.Error);
            StorageMap map = await StorageMapper.MapAsync(parse.GetValue(folder)!, new StorageMapOptions { ChildrenPerFolder = count }, progress, token).ConfigureAwait(false);
            progress.End();
            if (parse.GetValue(json))
            {
                var output = new StorageMapOutput(SchemaVersion, Cut(map.Root, levels), [.. map.Extensions.Take(count)], map.Inaccessible, map.Links, (long)map.Elapsed.TotalMilliseconds);
                Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.StorageMapOutput));
                return ExitCodes.Success;
            }

            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{map.Root.Path}: {TextFormat.Bytes(map.Root.Bytes)} in {map.Root.Files} files and {map.Root.Folders} folders ({map.Elapsed.TotalSeconds:0.0} s)"));
            WriteTree(map.Root, levels, 1);
            Console.Out.WriteLine("Extensions");
            foreach (ExtensionGroup group in map.Extensions.Take(count))
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {TextFormat.Pad(group.Extension, 16)} {TextFormat.Bytes(group.Bytes),10} {group.Files,9} files"));
            }

            if (map.Inaccessible + map.Links > 0)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{map.Inaccessible} folders could not be read; {map.Links} links and junctions were not followed."));
            }

            return ExitCodes.Success;
        }, cancellationToken));
        return command;
    }

    private static Command Duplicates(Option<bool> verbose)
    {
        var folders = new Argument<string[]>("folders") { Description = "Folders to compare.", Arity = ArgumentArity.OneOrMore };
        var minimum = new Option<long>("--min-size") { Description = "Ignore files smaller than this many bytes.", DefaultValueFactory = _ => 1 };
        var hidden = new Option<bool>("--hidden") { Description = "Include hidden and system files." };
        var keep = new Option<KeepRule>("--keep") { Description = "Which copy stays: Oldest or Newest.", DefaultValueFactory = _ => KeepRule.Oldest };
        var remove = new Option<bool>("--remove") { Description = "Remove the other copies; protected ones are never touched." };
        var dryRun = new Option<bool>("--dry-run") { Description = "With --remove, show what would go; change nothing." };
        var permanent = new Option<bool>("--permanent") { Description = "Delete permanently instead of moving to the Recycle Bin." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var json = new Option<bool>("--json") { Description = "Write the report as JSON to stdout." };
        var command = new Command("duplicates", "Duplicate Scan: same size, then the first and last 4 KB, then a full SHA-256.")
        {
            folders, minimum, hidden, keep, remove, dryRun, permanent, yes, json,
        };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("toolkit duplicates", parse.GetValue(verbose), async (host, token) =>
        {
            bool asJson = parse.GetValue(json);
            TextWriter o = asJson ? Console.Error : Console.Out;
            var options = new DuplicateOptions { MinimumBytes = Math.Max(1, parse.GetValue(minimum)), IncludeHidden = parse.GetValue(hidden) };
            DuplicateReport report = await host.CreateDuplicateFinder().FindAsync(parse.GetValue(folders)!, options, new DuplicateProgressWriter(Console.Error), token).ConfigureAwait(false);
            KeepRule rule = parse.GetValue(keep);
            IReadOnlyList<DuplicateFile> planned = DuplicateCleaner.Plan(report.Groups, rule);
            foreach (DuplicateGroup group in report.Groups)
            {
                DuplicateFile keeper = DuplicateSelection.Keeper(group, rule);
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{TextFormat.Bytes(group.Bytes)} x {group.Files.Count}  sha256 {group.Hash[..16]}…"));
                foreach (DuplicateFile file in group.Files)
                {
                    string mark = ReferenceEquals(file, keeper) ? "keep" : file.IsProtected ? "protected" : "remove";
                    o.WriteLine($"  {mark,-9} {file.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {file.Path}");
                }
            }

            o.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{report.Scanned} files scanned, {report.Groups.Count} groups, {planned.Count} copies to remove ({TextFormat.Bytes(planned.Sum(f => f.Bytes))}); {report.HardLinks} hard links and {report.Unreadable} unreadable files skipped."));
            if (!parse.GetValue(remove) || parse.GetValue(dryRun) || planned.Count == 0)
            {
                o.WriteLine(planned.Count == 0 ? "Nothing to remove." : parse.GetValue(remove) ? "Dry run: nothing was changed." : "Add --remove to remove the copies marked remove.");
                WriteJson(asJson, new DuplicateOutput(SchemaVersion, true, rule, report, planned, null));
                return ExitCodes.Success;
            }

            if (await Confirmation.RequireAsync($"Remove {planned.Count} copies?", parse.GetValue(yes), token).ConfigureAwait(false) is { } declined)
            {
                return declined;
            }

            DuplicateRemoval removal = await host.CreateDuplicateCleaner()
                .RemoveAsync(report.Groups, rule, parse.GetValue(permanent), new CleanText.Progress(Console.Error), token).ConfigureAwait(false);
            if (removal.Changed.Count > 0)
            {
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{removal.Changed.Count} copies changed since the scan and were kept."));
            }

            if (removal.Result is { } result)
            {
                CleanText.WriteTransaction(o, result.Transaction);
            }

            WriteJson(asJson, new DuplicateOutput(SchemaVersion, false, rule, report, planned, removal));
            return removal.Result is { } done ? CleanText.ExitCode(done.Transaction) : ExitCodes.Success;
        }, cancellationToken));
        return command;
    }

    private static Command Wipe(Option<bool> verbose)
    {
        var files = new Argument<string[]>("files") { Description = "Files to overwrite and delete.", Arity = ArgumentArity.OneOrMore };
        var method = MethodOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Check the files; change nothing." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var json = new Option<bool>("--json") { Description = "Write the results as JSON to stdout." };
        var command = new Command("wipe", "Secure Wipe: overwrite files, scramble their names and delete them. There is no backup and no undo.") { files, method, dryRun, yes, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("toolkit wipe", parse.GetValue(verbose), async (host, token) =>
        {
            string[] targets = parse.GetValue(files)!;
            WipeMethod chosen = parse.GetValue(method);
            bool asJson = parse.GetValue(json);
            TextWriter o = asJson ? Console.Error : Console.Out;
            int passes = WipePatterns.For(chosen).Count;
            foreach (string target in targets)
            {
                o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(File.Exists(target) ? TextFormat.Bytes(new FileInfo(target).Length) : "missing"),10}  {target}"));
            }

            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{chosen}: {passes} passes. Solid-state drives may keep old copies in spare blocks that no overwrite reaches."));
            if (parse.GetValue(dryRun))
            {
                o.WriteLine("Dry run: nothing was changed.");
                WriteJson(asJson, new WipeOutput(SchemaVersion, true, chosen, [], null));
                return ExitCodes.Success;
            }

            if (await Confirmation.RequireAsync($"Wipe {targets.Length} files for good?", parse.GetValue(yes), token).ConfigureAwait(false) is { } declined)
            {
                return declined;
            }

            SecureWiper wiper = host.CreateSecureWiper();
            List<WipeResult> results = [.. targets.Select(t => wiper.WipeFile(t, chosen, cancellationToken: token))];
            foreach (WipeResult result in results)
            {
                o.WriteLine($"  {result.Outcome,-8} {result.Path}{(result.Detail is null ? string.Empty : $"  [{result.Detail}]")}");
            }

            WriteJson(asJson, new WipeOutput(SchemaVersion, false, chosen, results, null));
            return results.All(r => r.Outcome == WipeOutcome.Wiped) ? ExitCodes.Success : ExitCodes.Failure;
        }, cancellationToken));
        return command;
    }

    private static Command WipeFree(Option<bool> verbose)
    {
        var drive = new Argument<string>("drive") { Description = "Drive whose free space to overwrite, such as D:." };
        var method = MethodOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Show how much would be written; change nothing." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };
        var command = new Command("wipe-free", "Secure Wipe of free space: fill the drive with pattern files pass by pass, then delete them.") { drive, method, dryRun, yes, json };
        command.SetAction((parse, cancellationToken) => CommandRunner.RunAsync("toolkit wipe-free", parse.GetValue(verbose), async (host, token) =>
        {
            WipeMethod chosen = parse.GetValue(method);
            bool asJson = parse.GetValue(json);
            TextWriter o = asJson ? Console.Error : Console.Out;
            string root = Path.GetPathRoot(Path.GetFullPath(parse.GetValue(drive)!)) ?? parse.GetValue(drive)!;
            var info = new DriveInfo(root);
            int passes = WipePatterns.For(chosen).Count;
            o.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{root}: {TextFormat.Bytes(info.IsReady ? info.AvailableFreeSpace : null)} free; {chosen} writes it {passes} times. The drive is full while this runs."));
            if (parse.GetValue(dryRun))
            {
                o.WriteLine("Dry run: nothing was changed.");
                WriteJson(asJson, new WipeOutput(SchemaVersion, true, chosen, [], null));
                return ExitCodes.Success;
            }

            if (await Confirmation.RequireAsync($"Overwrite the free space of {root}?", parse.GetValue(yes), token).ConfigureAwait(false) is { } declined)
            {
                return declined;
            }

            FreeSpaceResult result = await host.CreateSecureWiper().WipeFreeSpaceAsync(root, chosen, progress: new WipeProgressWriter(Console.Error), cancellationToken: token).ConfigureAwait(false);
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{result.Outcome}: {TextFormat.Bytes(result.Bytes)} overwritten {result.Passes} times{(result.Detail is null ? string.Empty : $"; {result.Detail}")}."));
            WriteJson(asJson, new WipeOutput(SchemaVersion, false, chosen, [], result));
            return result.Outcome switch
            {
                WipeOutcome.Wiped => ExitCodes.Success,
                WipeOutcome.Cancelled => ExitCodes.Cancelled,
                _ => ExitCodes.Failure,
            };
        }, cancellationToken));
        return command;
    }

    private static Option<WipeMethod> MethodOption() => new("--method")
    {
        Description = "ZeroFill (1 pass), Dod522022M (3 passes) or Gutmann (35 passes).",
        DefaultValueFactory = _ => WipeMethod.ZeroFill,
    };

    private static StorageNode Cut(StorageNode node, int levels) =>
        node with { Children = levels <= 0 ? [] : [.. node.Children.Select(c => Cut(c, levels - 1))] };

    private static void WriteTree(StorageNode node, int levels, int indent)
    {
        if (levels <= 0)
        {
            return;
        }

        long total = Math.Max(1, node.Bytes);
        foreach (StorageNode child in node.Children)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{new string(' ', indent * 2)}{TextFormat.Bytes(child.Bytes),10} {child.Bytes * 100.0 / total,5:0.0}%  {child.Name}{(child.Inaccessible ? "  [unreadable]" : string.Empty)}"));
            WriteTree(child, levels - 1, indent + 1);
        }
    }

    private static void WriteJson(bool asJson, DuplicateOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.DuplicateOutput));
        }
    }

    private static void WriteJson(bool asJson, WipeOutput output)
    {
        if (asJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.WipeOutput));
        }
    }

    private sealed class MapProgress(TextWriter writer) : IProgress<StorageProgress>
    {
        private bool _shown;

        public void Report(StorageProgress value)
        {
            _shown = true;
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"\r{value.Folders} folders, {value.Files} files, {TextFormat.Bytes(value.Bytes)}   "));
        }

        public void End()
        {
            if (_shown)
            {
                writer.WriteLine();
            }
        }
    }

    private sealed class DuplicateProgressWriter(TextWriter writer) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) =>
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"\r{value.Stage}: {value.Done}/{value.Total}   {(value.Done == value.Total ? Environment.NewLine : string.Empty)}"));
    }

    private sealed class WipeProgressWriter(TextWriter writer) : IProgress<WipeProgress>
    {
        private const long Step = 256L << 20;
        private long _next;
        private int _pass;

        public void Report(WipeProgress value)
        {
            if (value.Pass != _pass)
            {
                _pass = value.Pass;
                _next = 0;
            }

            if (value.Written >= _next)
            {
                _next = value.Written + Step;
                writer.Write(string.Create(CultureInfo.InvariantCulture, $"\rpass {value.Pass}/{value.Passes}: {TextFormat.Bytes(value.Written)}   "));
            }
        }
    }
}
