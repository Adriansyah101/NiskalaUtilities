using System.CommandLine;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Scanner.Inventory;

namespace Niskala.Cli.Commands;

public enum SourceFilter
{
    All,
    Win32,
    Store,
}

internal sealed record ListOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool Elevated,
    long ElapsedMs,
    int Total,
    int Visible,
    IReadOnlyList<InstalledApp> Apps);

internal static class ListCommand
{
    private const int SchemaVersion = 2;

    public static Command Create(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write the inventory as JSON to stdout." };
        var all = new Option<bool>("--all") { Description = "Include hidden entries (system components, updates, duplicates, sparse packages)." };
        var source = new Option<SourceFilter>("--source") { Description = "Sources to read: all, win32 (Uninstall entries and Windows Installer) or store (Store apps: MSIX and AppX packages).", DefaultValueFactory = _ => SourceFilter.All };
        var noMsiIndex = new Option<bool>("--no-msi-index")
        {
            Description = "Use cached MSI install folders only; never build the component index.",
        };

        var command = new Command("list", "List installed applications: Uninstall keys, Windows Installer and Store apps (MSIX and AppX packages).")
        {
            json, all, source, noMsiIndex,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            var options = new InventoryOptions
            {
                Sources = parse.GetValue(source) switch
                {
                    SourceFilter.Win32 => AppSources.Registry | AppSources.Msi,
                    SourceFilter.Store => AppSources.Appx,
                    _ => AppSources.Registry | AppSources.Msi | AppSources.Appx,
                },
                BuildMsiComponentIndex = !parse.GetValue(noMsiIndex),
            };

            bool asJson = parse.GetValue(json);
            bool includeHidden = parse.GetValue(all);
            return CommandRunner.RunAsync("list", parse.GetValue(verbose), (host, token) =>
            {
                InventorySnapshot snapshot = host.CreateInventoryCollector().Collect(options, token);
                List<InstalledApp> apps = [.. snapshot.Apps.Where(a => includeHidden || a.IsVisible)];
                if (asJson)
                {
                    WriteJson(snapshot, apps);
                }
                else
                {
                    WriteTable(snapshot, apps, includeHidden);
                }

                return Task.FromResult(ExitCodes.Success);
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteJson(InventorySnapshot snapshot, List<InstalledApp> apps)
    {
        var output = new ListOutput(
            SchemaVersion,
            DateTimeOffset.Now,
            Environment.IsPrivilegedProcess,
            (long)snapshot.Elapsed.TotalMilliseconds,
            snapshot.Apps.Count,
            snapshot.VisibleCount,
            apps);
        Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.ListOutput));
    }

    private static void WriteTable(InventorySnapshot snapshot, List<InstalledApp> apps, bool includeHidden)
    {
        const int Name = 38, Version = 16, Publisher = 24, Kind = 14, Scope = 7, Size = 9;
        TextWriter o = Console.Out;
        o.WriteLine($"{TextFormat.Pad("Name", Name)} {TextFormat.Pad("Version", Version)} {TextFormat.Pad("Publisher", Publisher)} {TextFormat.Pad("Installer", Kind)} {TextFormat.Pad("Scope", Scope)} {"Size",Size}");
        o.WriteLine(new string('-', Name + Version + Publisher + Kind + Scope + Size + 5));

        foreach (InstalledApp app in apps)
        {
            string name = app.DisplayName ?? $"({app.KeyName ?? app.Id})";
            if (app.IsOrphan || app.IsUninstallerMissing)
            {
                name = "! " + name;
            }

            if (app.IsProtected)
            {
                name = "# " + name;
            }

            if (includeHidden && !app.IsVisible)
            {
                name = $"[{app.HiddenReason}] {name}";
            }

            o.WriteLine(
                $"{TextFormat.Pad(name, Name)} {TextFormat.Pad(app.DisplayVersion, Version)} {TextFormat.Pad(app.Publisher, Publisher)} "
                + $"{TextFormat.Pad(app.InstallerKind.ToString(), Kind)} {TextFormat.Pad(app.Scope.ToString(), Scope)} {TextFormat.Bytes(app.EstimatedSizeBytes),Size}");
        }

        o.WriteLine();
        o.WriteLine(
            $"{apps.Count} shown, {snapshot.VisibleCount} visible of {snapshot.Apps.Count} entries, {snapshot.Elapsed.TotalMilliseconds:0} ms. "
            + "\"!\" no or missing uninstaller, \"#\" protected. Sizes are the installer's estimate.");
    }
}
