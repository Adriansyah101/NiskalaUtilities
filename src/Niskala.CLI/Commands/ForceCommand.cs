using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian.Journal;
using Niskala.Host;
using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Picker;
using Niskala.Uninstall.Preflight;

namespace Niskala.Cli.Commands;

internal sealed record ForceOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool DryRun,
    InstalledApp App,
    bool InInventory,
    PickerHit? Picked,
    IReadOnlyList<RunningProcess> Processes,
    IReadOnlyList<string> Services,
    IReadOnlyList<LeftoverItem> Selected,
    ForceResult? Result);

internal static class ForceCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose)
    {
        var target = new Argument<string?>("app") { Description = "Id or name of an installed application, its install folder, or a program or shortcut (.exe, .lnk, .url) of it.", Arity = ArgumentArity.ZeroOrOne };
        var at = new Option<string?>("--at") { Description = "Target Picker: take the application whose window, desktop icon or tray icon is at screen point X,Y." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the processes, services and leftovers; change nothing." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation; empty folders are removed as well." };
        var permanent = new Option<bool>("--permanent") { Description = "Delete files permanently instead of moving them to the Recycle Bin." };
        var review = new Option<bool>("--review") { Description = "Also remove items in the Review tier." };
        var noRestorePoint = new Option<bool>("--no-restore-point") { Description = "Skip the System Restore point; the registry backup is still made." };
        var all = new Option<bool>("--all") { Description = "Also match hidden inventory entries." };
        var json = new Option<bool>("--json") { Description = "Write the plan and the result as JSON to stdout; progress goes to stderr." };

        var command = new Command("force", "Remove an application without its uninstaller: end its processes, stop its services, then remove what it owns.")
        {
            target, at, dryRun, yes, permanent, review, noRestorePoint, all, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string? query = parse.GetValue(target);
            string? point = parse.GetValue(at);
            bool isDryRun = parse.GetValue(dryRun);
            bool assumeYes = parse.GetValue(yes);
            var options = new ForceOptions
            {
                Permanent = parse.GetValue(permanent),
                CreateRestorePoint = !parse.GetValue(noRestorePoint),
                IncludeReview = parse.GetValue(review),
            };
            bool includeHidden = parse.GetValue(all);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("force", parse.GetValue(verbose), async (host, token) =>
            {
                if ((query is null) == (point is null))
                {
                    await Console.Error.WriteLineAsync("Name an application or folder, or pass --at X,Y.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                IReadOnlyList<InstalledApp> installed = host.CreateInventoryCollector().Collect(cancellationToken: token).Apps;
                TextWriter o = asJson ? Console.Error : Console.Out;
                (InstalledApp? app, bool inInventory, PickerHit? hit, string? problem) = point is not null
                    ? PickAt(point, installed, o)
                    : Resolve(query!, installed, includeHidden, host);
                if (app is null)
                {
                    await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                if (app.IsProtected || app.Package is not null)
                {
                    await Console.Error.WriteLineAsync($"{app.DisplayName ?? app.Id} is a package{(app.IsProtected ? " that Windows needs" : string.Empty)}; use niskala uninstall.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                ForceRemover remover = host.CreateForceRemover();
                ForcePlan plan = remover.Plan(app, inInventory, installed, token);
                IReadOnlyList<LeftoverItem> items = ForceRemover.Select(app, plan.Leftovers, options.IncludeReview);
                WritePlan(o, plan, items, options, host.CreateLeftoverCleaner().PredictEmptyParents(items));
                if (isDryRun)
                {
                    o.WriteLine("Dry run: nothing was changed.");
                    WriteJson(asJson, plan, hit, items, null, dryRun: true);
                    return ExitCodes.Success;
                }

                if (await Confirmation.RequireAsync($"Force-remove {app.DisplayName ?? app.Id}?", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                ForceResult result = await remover.ExecuteAsync(plan, installed, options, new CleanText.Progress(Console.Error), token).ConfigureAwait(false);
                WriteSteps(o, result);
                TransactionRecord transaction = await CleanText.OfferEmptyFoldersAsync(host.CreateLeftoverCleaner(), result.Clean, assumeYes, o, token).ConfigureAwait(false);
                CleanText.WriteTransaction(o, transaction);
                WriteJson(asJson, plan, hit, items, result with { Clean = result.Clean with { Transaction = transaction } }, dryRun: false);
                return CleanText.ExitCode(transaction);
            }, cancellationToken);
        });

        return command;
    }

    private static (InstalledApp?, bool, PickerHit?, string?) PickAt(string point, IReadOnlyList<InstalledApp> installed, TextWriter o)
    {
        string[] parts = point.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            return (null, false, null, "--at takes screen coordinates as X,Y, for example --at 640,400.");
        }

        var picker = new WindowPicker(SystemWindowProbe.Instance);
        picker.ReadDesktop();
        PickerHit? hit = picker.PickAt(x, y, installed);
        if (hit is null)
        {
            return (null, false, null, string.Create(CultureInfo.InvariantCulture, $"Nothing that can be named is at {x},{y}."));
        }

        o.WriteLine(hit.Kind switch
        {
            PickerTargetKind.DesktopIcon => $"Desktop icon \"{hit.WindowTitle}\", {hit.ItemPath ?? "not a file"} -> {hit.ImagePath ?? "(no program)"}",
            PickerTargetKind.TrayIcon => string.Create(CultureInfo.InvariantCulture, $"Tray icon of process {hit.ProcessId} {hit.ImagePath ?? "(unreadable)"}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"Window \"{hit.WindowTitle}\" ({hit.WindowClass}), process {hit.ProcessId} {hit.ImagePath ?? "(unreadable)"}"),
        });
        return Owner(hit);
    }

    private static (InstalledApp?, bool, PickerHit?, string?) Owner(PickerHit hit) => hit.App is null
        ? (null, false, hit, $"No installed application owns {hit.ImagePath ?? hit.ItemPath ?? "that target"}.")
        : (hit.App, true, hit, null);

    private static (InstalledApp?, bool, PickerHit?, string?) Resolve(string query, IReadOnlyList<InstalledApp> installed, bool includeHidden, NiskalaHost host)
    {
        if (Path.IsPathFullyQualified(query) && File.Exists(query) && Path.GetExtension(query).ToUpperInvariant() is ".EXE" or ".LNK" or ".URL")
        {
            PickerHit hit = TargetResolver.FromFile(query, installed);
            if (hit.App is not null || Path.GetDirectoryName(hit.ImagePath) is not { } folder)
            {
                return Owner(hit);
            }

            query = folder;
        }

        if (Path.IsPathFullyQualified(query))
        {
            return ForceRemover.ForPath(query, host.PathGuard, installed, host.KnownRoots, out PathDecision decision) is { } target
                ? (target.App, target.InInventory, null, null)
                : (null, false, null, decision.IsAllowed ? $"{query} is not an existing folder." : $"{query} may not be removed: {decision}.");
        }

        List<InstalledApp> matches = AppQuery.Find(installed.Where(a => includeHidden || a.IsVisible), query);
        if (matches.Count == 1)
        {
            return (matches[0], true, null, null);
        }

        string list = matches.Count == 0
            ? $"No application matches \"{query}\". Pass its install folder, or try --all."
            : $"\"{query}\" matches {matches.Count} applications; pass one of these ids:{Environment.NewLine}"
                + string.Join(Environment.NewLine, matches.Take(24).Select(a => $"  {a.Id}  {a.DisplayName} {a.DisplayVersion}"));
        return (null, false, null, list);
    }

    private static void WritePlan(TextWriter o, ForcePlan plan, IReadOnlyList<LeftoverItem> items, ForceOptions options, IReadOnlyList<string> emptyParents)
    {
        InstalledApp app = plan.App;
        o.WriteLine($"{app.DisplayName ?? app.Id} {app.DisplayVersion} ({(plan.InInventory ? app.Id : "folder, not in the inventory")})");
        o.WriteLine(plan.Fingerprint.Roots.Count == 0 ? "  Folder     none known" : $"  Folder     {plan.Fingerprint.Roots[^1]}");
        foreach (string note in plan.Fingerprint.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        if (app.MsiProductCode is { } product)
        {
            o.WriteLine($"  ! Windows Installer keeps its own record of {product:B}, which force does not remove; niskala uninstall is the clean way.");
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Processes to end ({plan.Processes.Count}):"));
        foreach (RunningProcess process in plan.Processes)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {process.Id,6}  {process.ImagePath}"));
        }

        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Services to stop and delete ({plan.Services.Count}):"));
        foreach (var service in plan.Services)
        {
            o.WriteLine($"  {service.Name}  {service.ImagePath}");
        }

        CleanText.WritePlan(o, items, emptyParents, options.Permanent, options.CreateRestorePoint);
        int left = plan.Leftovers.Items.Count - items.Count;
        if (left > 0)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {left} lower-confidence matches stay{(options.IncludeReview ? string.Empty : "; --review adds the Review tier")}; niskala leftovers lists them."));
        }
    }

    private static void WriteSteps(TextWriter o, ForceResult result)
    {
        foreach (ProcessStop stop in result.Processes)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  process {stop.Process.Id} {stop.Process.Name}: {stop.Result.Outcome}{(stop.Result.Error == 0 ? string.Empty : $" (Win32 error {stop.Result.Error})")}"));
        }

        foreach (ServiceStop stop in result.Services)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  service {stop.Name}: {(stop.Error == 0 ? "stopped" : $"Win32 error {stop.Error}")}"));
        }
    }

    private static void WriteJson(bool asJson, ForcePlan plan, PickerHit? hit, IReadOnlyList<LeftoverItem> items, ForceResult? result, bool dryRun)
    {
        if (!asJson)
        {
            return;
        }

        var output = new ForceOutput(
            SchemaVersion,
            DateTimeOffset.Now,
            dryRun,
            plan.App,
            plan.InInventory,
            hit,
            plan.Processes,
            [.. plan.Services.Select(s => s.Name)],
            items,
            result);
        Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.ForceOutput));
    }
}
