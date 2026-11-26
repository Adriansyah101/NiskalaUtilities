using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Host;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Sizing;

namespace Niskala.Cli.Commands;

internal sealed record SizeOutput(long ElapsedMs, long TotalBytes, IReadOnlyList<FolderSize> Folders);

internal static class SizeCommand
{
    public static Command Create(Option<bool> verbose)
    {
        var paths = new Argument<string[]>("paths") { Description = "Folders to measure.", Arity = ArgumentArity.ZeroOrMore };
        var installed = new Option<bool>("--installed") { Description = "Measure the install folder of every visible application." };
        var json = new Option<bool>("--json") { Description = "Write the results as JSON to stdout." };

        var command = new Command("size", "Measure folder sizes (junctions and symbolic links are not followed).")
        {
            paths, installed, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string[] folders = parse.GetValue(paths) ?? [];
            bool fromInventory = parse.GetValue(installed);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("size", parse.GetValue(verbose), async (host, token) =>
            {
                List<SizeRequest> requests = [.. folders.Select(folder => new SizeRequest(folder, Path.GetFullPath(folder)))];
                if (fromInventory)
                {
                    requests.AddRange(InstalledFolders(host, token));
                }

                if (requests.Count == 0)
                {
                    await Console.Error.WriteLineAsync("Nothing to measure: pass folders or --installed.").ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                long start = Stopwatch.GetTimestamp();
                var results = new List<FolderSize>(requests.Count);
                await foreach (FolderSize result in NiskalaHost.CreateFolderSizeService().MeasureAllAsync(requests, token).ConfigureAwait(false))
                {
                    results.Add(result);
                    if (!asJson)
                    {
                        WriteRow(result);
                    }
                }

                var output = new SizeOutput((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, results.Sum(r => r.Bytes), results);
                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.SizeOutput));
                }
                else
                {
                    WriteSummary(output);
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static IEnumerable<SizeRequest> InstalledFolders(NiskalaHost host, CancellationToken cancellationToken) =>
        host.CreateInventoryCollector()
            .Collect(new InventoryOptions { BuildMsiComponentIndex = false }, cancellationToken)
            .Apps
            .Where(app => app is { IsVisible: true, InstallLocation: not null }
                && (app.InstallLocationGuard == PathVerdict.Allowed || app.Sources.HasFlag(AppSources.Appx)))
            .DistinctBy(app => app.InstallLocation, StringComparer.OrdinalIgnoreCase)
            .Select(app => new SizeRequest(app.Id, app.InstallLocation!));

    private static void WriteRow(FolderSize result)
    {
        string state = result switch
        {
            { Error: not null } => "error",
            { Exists: false } => "missing",
            { IsLink: true } => "link",
            _ => TextFormat.Bytes(result.Bytes),
        };

        Console.Out.WriteLine(
            $"{state,9} {result.Files,8} files {result.Directories,7} dirs {result.Elapsed.TotalMilliseconds,7:0} ms  {result.Disk,-7} {result.Path}");
    }

    private static void WriteSummary(SizeOutput output)
    {
        Console.Out.WriteLine();
        string disks = string.Join(", ", output.Folders
            .GroupBy(r => r.Disk)
            .Select(g => $"{g.Key}: {g.Count()} folders, {DiskKindProbe.WorkersFor(g.Key)} workers per volume"));
        Console.Out.WriteLine(
            $"{output.Folders.Count} folders, {TextFormat.Bytes(output.TotalBytes)} in {output.ElapsedMs} ms "
            + $"(sum of folder times {output.Folders.Sum(r => r.Elapsed.TotalMilliseconds):0} ms). {disks}.");
    }
}
