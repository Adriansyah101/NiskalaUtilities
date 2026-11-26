using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Core.Models;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Inventory;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Preflight;

namespace Niskala.Cli.Commands;

internal sealed record InspectOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    InstalledApp App,
    CommandResolution Interactive,
    CommandResolution Silent,
    PreflightReport Preflight,
    Fingerprint Fingerprint);

internal static class InspectCommand
{
    private const int SchemaVersion = 1;
    private const int ListLimit = 12;

    public static Command Create(Option<bool> verbose)
    {
        var target = new Argument<string>("app") { Description = "Id, exact name or part of the name of an installed application." };
        var silent = new Option<bool>("--silent") { Description = "Run the preflight checks for a silent uninstall." };
        var all = new Option<bool>("--all") { Description = "Also match hidden entries." };
        var files = new Option<bool>("--files") { Description = "Include the install folder's file list in the JSON output." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };

        var command = new Command("inspect", "Resolve the uninstall command, run the preflight checks and take a fingerprint. Nothing is removed.")
        {
            target, silent, all, files, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string query = parse.GetValue(target)!;
            UninstallMode mode = parse.GetValue(silent) ? UninstallMode.Silent : UninstallMode.Interactive;
            bool includeHidden = parse.GetValue(all);
            bool withFiles = parse.GetValue(files);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("inspect", parse.GetValue(verbose), async (host, token) =>
            {
                IReadOnlyList<InstalledApp> apps = host.CreateInventoryCollector().Collect(cancellationToken: token).Apps;
                List<InstalledApp> matches = AppQuery.Find(apps.Where(a => includeHidden || a.IsVisible), query);
                if (matches.Count != 1)
                {
                    await AppQuery.ReportNoSingleMatch(query, matches, "Try niskala list, or --all for hidden entries.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                InstalledApp app = matches[0];
                CommandLineResolver resolver = host.CreateCommandLineResolver();
                CommandResolution interactive = resolver.Resolve(app, UninstallMode.Interactive);
                CommandResolution quiet = resolver.Resolve(app, UninstallMode.Silent);
                PreflightReport preflight = host.CreatePreflight().Run(app, mode == UninstallMode.Silent ? quiet : interactive);
                Fingerprint fingerprint = host.CreateFingerprintCollector().Collect(app, cancellationToken: token);

                if (asJson)
                {
                    Fingerprint shown = withFiles || fingerprint.Files is null ? fingerprint : fingerprint with { Files = fingerprint.Files with { Entries = [] } };
                    var output = new InspectOutput(SchemaVersion, DateTimeOffset.Now, app, interactive, quiet, preflight, shown);
                    Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.InspectOutput));
                }
                else
                {
                    WriteText(app, interactive, quiet, preflight, fingerprint, mode);
                }

                return preflight.CanProceed ? ExitCodes.Success : ExitCodes.Failure;
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteText(InstalledApp app, CommandResolution interactive, CommandResolution silent, PreflightReport preflight, Fingerprint fingerprint, UninstallMode mode)
    {
        TextWriter o = Console.Out;
        o.WriteLine($"{app.DisplayName ?? app.Id} {app.DisplayVersion} ({app.Publisher ?? "unknown publisher"})");
        Field("Id", app.Id);
        Field("Installer", app.DetectionEvidence.Count == 0 ? app.InstallerKind.ToString() : $"{app.InstallerKind}: {string.Join("; ", app.DetectionEvidence)}");
        Field("Scope", app.Scope.ToString());
        Field("Install folder", app.InstallLocation is null ? "-" : $"{app.InstallLocation} [{app.InstallLocationGuard?.ToString() ?? "unchecked"}, {app.InstallLocationOrigin}]");

        WriteResolution("Interactive uninstall", interactive);
        WriteResolution("Silent uninstall", silent);

        o.WriteLine();
        o.WriteLine($"Preflight ({mode}, elevated: {(preflight.IsElevated ? "yes" : "no")}): {(preflight.CanProceed ? "can proceed" : "blocked")}");
        foreach (PreflightIssue issue in preflight.Issues)
        {
            o.WriteLine($"  [{issue.Severity}] {issue.Check}: {issue.Message}");
        }

        o.WriteLine();
        o.WriteLine($"Fingerprint ({fingerprint.Elapsed.TotalMilliseconds:0} ms)");
        Field("Roots", fingerprint.Roots.Count == 0 ? "-" : string.Join("; ", fingerprint.Roots));
        if (fingerprint.Files is { } tree)
        {
            Field("Files", string.Create(CultureInfo.InvariantCulture, $"{tree.Files:N0} files, {tree.Directories:N0} folders, {TextFormat.Bytes(tree.Bytes)}"));
        }

        Field("Executables", Joined(fingerprint.ExecutableNames));
        if (fingerprint.MsiProductCode is { } product)
        {
            Field("MSI", $"ProductCode {Code(product)}, UpgradeCode {(fingerprint.MsiUpgradeCode is { } upgrade ? Code(upgrade) : "-")}");
        }

        if (fingerprint.InnoAppId is { } innoId)
        {
            Field("Inno AppId", innoId);
        }

        List("Services", fingerprint.Services, s => $"{s.Name}  {s.ServiceDll ?? s.ImagePath}");
        List("Run", fingerprint.RunEntries, r => $"{r.Key}  {r.ValueName} = {r.Data}");
        List("App Paths", fingerprint.AppPaths, r => $"{r.Key}  {r.ValueName ?? "(default)"} = {r.Data}");
        List("Tasks", fingerprint.ScheduledTasks, t => $"{t.Path}  {t.Command} {t.Arguments}".TrimEnd());
        List("Shortcuts", fingerprint.Shortcuts, s => s.MsiProductCode is { } code ? $"{s.LinkPath}  (advertised {Code(code)})" : $"{s.LinkPath}  -> {s.Target} {s.Arguments}".TrimEnd());
        foreach (string note in fingerprint.Notes)
        {
            o.WriteLine($"  note: {note}");
        }

        void Field(string name, string value) => o.WriteLine($"  {name,-15}{value}");

        void WriteResolution(string title, CommandResolution resolution)
        {
            o.WriteLine();
            o.WriteLine(resolution switch
            {
                { Command: { } c } => $"{title} ({c.Origin}{(c.IsSilent ? ", silent" : string.Empty)}{(resolution.Issue == ResolutionIssue.None ? string.Empty : $", {resolution.Issue}")})",
                { PackageFullName: { } package } => $"{title} (PackageManager): remove {package}",
                _ => $"{title}: {resolution.Issue}",
            });
            if (resolution.Command is { } command)
            {
                o.WriteLine($"  {command.CommandLine}");
            }

            foreach (string note in resolution.Notes)
            {
                o.WriteLine($"  note: {note}");
            }

            foreach (CommandWarning warning in resolution.Warnings)
            {
                o.WriteLine($"  ! {warning.Kind}: {warning.Message}");
            }
        }

        void List<T>(string name, IReadOnlyList<T> items, Func<T, string> format)
        {
            if (items.Count == 0)
            {
                Field(name, "-");
                return;
            }

            for (int i = 0; i < Math.Min(items.Count, ListLimit); i++)
            {
                Field(i == 0 ? name : string.Empty, format(items[i]));
            }

            if (items.Count > ListLimit)
            {
                Field(string.Empty, $"(+{items.Count - ListLimit} more, see --json)");
            }
        }
    }

    private static string Code(Guid code) => code.ToString("B").ToUpperInvariant();

    private static string Joined(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "-",
        <= ListLimit => string.Join(", ", names),
        _ => $"{string.Join(", ", names.Take(ListLimit))} (+{names.Count - ListLimit})",
    };
}
