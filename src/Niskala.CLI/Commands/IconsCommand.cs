using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Scanner.Icons;
using Niskala.Scanner.Inventory;

namespace Niskala.Cli.Commands;

internal sealed record IconEntry(string Id, string? DisplayName, string? IconPath);

internal sealed record IconsOutput(long ElapsedMs, int Found, int Missing, int Pruned, string Directory, IReadOnlyList<IconEntry> Icons);

internal static class IconsCommand
{
    public static Command Create(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write the icon paths as JSON to stdout." };
        var prune = new Option<bool>("--prune") { Description = "Delete cached icons that no installed application maps to." };
        var command = new Command("icons", "Extract the 256 px icon of every visible application into the icon cache.") { json, prune };

        command.SetAction((parse, cancellationToken) =>
        {
            bool asJson = parse.GetValue(json);
            bool pruneCache = parse.GetValue(prune);
            return CommandRunner.RunAsync("icons", parse.GetValue(verbose), async (host, token) =>
            {
                IReadOnlyList<InstalledApp> apps = host.CreateInventoryCollector()
                    .Collect(new InventoryOptions { BuildMsiComponentIndex = false }, token)
                    .Apps;

                long start = Stopwatch.GetTimestamp();
                using var extractor = new IconExtractor();
                IconCache cache = host.CreateIconCache(extractor);
                IconEntry[] icons = await Task.WhenAll(apps
                    .Where(app => app.IsVisible)
                    .Select(async app => new IconEntry(app.Id, app.DisplayName, await cache.GetAsync(app, token).ConfigureAwait(false))))
                    .ConfigureAwait(false);

                int pruned = pruneCache ? cache.Prune(apps) : 0;
                int found = icons.Count(icon => icon.IconPath is not null);
                var output = new IconsOutput(
                    (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, found, icons.Length - found, pruned, IconCache.DefaultDirectory, icons);

                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.IconsOutput));
                }
                else
                {
                    foreach (IconEntry icon in icons.Where(icon => icon.IconPath is null))
                    {
                        Console.Out.WriteLine($"no icon: {icon.DisplayName ?? icon.Id}");
                    }

                    Console.Out.WriteLine($"{found} icons, {output.Missing} without icon, {pruned} pruned, {output.ElapsedMs} ms. Cache: {output.Directory}");
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }
}
