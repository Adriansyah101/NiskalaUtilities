using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Updater;

namespace Niskala.Cli.Commands;

internal sealed record UpdateCheckOutput(
    int SchemaVersion,
    string Current,
    InstallMode Mode,
    UpdateStatus Status,
    string? Latest,
    string? Title,
    DateTimeOffset? Published,
    Uri? Page,
    string? Package,
    string? Notes);

internal sealed record UpdateInstallOutput(
    int SchemaVersion,
    string Current,
    InstallMode Mode,
    UpdateStatus Status,
    string? Latest,
    bool DryRun,
    string? Package,
    string? Sha256,
    UpdateAction? Action,
    string? Path);

internal static class UpdateCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose) =>
        new("update", "Check GitHub for a newer release and install it once its SHA-256 checksum matches.")
        {
            Check(verbose), Install(verbose),
        };

    private static Command Check(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout." };
        var command = new Command("check", "Show the running version, the latest release and its notes.") { json };
        command.SetAction((parse, cancellationToken) =>
        {
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("update check", parse.GetValue(verbose), async (host, token) =>
            {
                using UpdateService updater = host.CreateUpdateService(UpdateProduct.CommandLine);
                UpdateCheck check = await updater.CheckAsync(token).ConfigureAwait(false);
                if (asJson)
                {
                    var output = new UpdateCheckOutput(
                        SchemaVersion,
                        check.Current.ToString(),
                        updater.Install.Mode,
                        check.Status,
                        check.Latest?.ToString(),
                        check.Title,
                        check.Published,
                        check.Page,
                        updater.PackageFor(check)?.Name,
                        check.Notes);
                    Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.UpdateCheckOutput));
                    return ExitCodes.Success;
                }

                UpdateText.WriteCheck(Console.Out, updater, check);
                if (check.IsAvailable)
                {
                    if (!string.IsNullOrEmpty(check.Notes))
                    {
                        Console.Out.WriteLine();
                        Console.Out.WriteLine(check.Notes);
                        Console.Out.WriteLine();
                    }

                    Console.Out.WriteLine("Run niskala update install to get it.");
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static Command Install(Option<bool> verbose)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Download and verify the package, then delete it; nothing is run or saved." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask before the installer runs." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout; progress goes to stderr." };
        var command = new Command(
            "install",
            "Download the latest release and check it against SHA256SUMS.txt; an installed copy runs the installer, a portable copy saves the new package to Downloads.")
        {
            dryRun, yes, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            bool isDryRun = parse.GetValue(dryRun);
            bool assumeYes = parse.GetValue(yes);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("update install", parse.GetValue(verbose), async (host, token) =>
            {
                using UpdateService updater = host.CreateUpdateService(UpdateProduct.CommandLine);
                UpdateCheck check = await updater.CheckAsync(token).ConfigureAwait(false);
                TextWriter o = asJson ? Console.Error : Console.Out;
                UpdateText.WriteCheck(o, updater, check);
                ReleaseAsset? package = updater.PackageFor(check);
                if (!check.IsAvailable)
                {
                    WriteJson(asJson, updater, check, isDryRun, package, null, null);
                    return ExitCodes.Success;
                }

                if (package is null)
                {
                    await Console.Error.WriteLineAsync($"Release {check.Latest} has no {UpdateText.PackageKind(updater.Install.Mode)}.").ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                if (!isDryRun && updater.Install.IsInstalled
                    && await Confirmation.RequireAsync($"Run the {check.Latest} installer now? It closes Niskala Utilities while it replaces it.", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                var progress = new ProgressLine(Console.Error, live: !Console.IsErrorRedirected);
                VerifiedDownload download = await updater.DownloadAsync(check, progress, token).ConfigureAwait(false);
                progress.End();
                o.WriteLine($"{package.Name}: SHA-256 {download.Sha256} matches {ReleaseNames.Checksums}.");

                UpdateLaunch? launch = null;
                if (isDryRun)
                {
                    UpdateService.Discard(download);
                    o.WriteLine("Dry run: the package was deleted; nothing was run or saved.");
                }
                else
                {
                    launch = updater.Apply(download, restart: false);
                    o.WriteLine(launch.Action == UpdateAction.InstallerStarted
                        ? "The installer is running; it replaces this copy on its own."
                        : $"Saved to {launch.Path}. Close niskala, then unzip it over {updater.Install.Root}.");
                }

                WriteJson(asJson, updater, check, isDryRun, package, download.Sha256, launch);
                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static void WriteJson(bool asJson, UpdateService updater, UpdateCheck check, bool dryRun, ReleaseAsset? package, string? sha256, UpdateLaunch? launch)
    {
        if (!asJson)
        {
            return;
        }

        var output = new UpdateInstallOutput(
            SchemaVersion,
            check.Current.ToString(),
            updater.Install.Mode,
            check.Status,
            check.Latest?.ToString(),
            dryRun,
            package?.Name,
            sha256,
            launch?.Action,
            launch?.Path);
        Console.Out.WriteLine(JsonSerializer.Serialize(output, CliJson.Output.UpdateInstallOutput));
    }

    private sealed class ProgressLine(TextWriter writer, bool live) : IProgress<DownloadProgress>
    {
        private int _shown;

        public void Report(DownloadProgress value)
        {
            if (!live)
            {
                return;
            }

            string percent = value.Percent is { } p ? string.Create(CultureInfo.InvariantCulture, $" ({p:0}%)") : string.Empty;
            string total = value.Total is { } t ? $" of {TextFormat.Bytes(t)}" : string.Empty;
            string text = $"Downloading {value.Name}: {TextFormat.Bytes(value.Received)}{total}{percent}";
            writer.Write('\r');
            writer.Write(text.Length < _shown ? text.PadRight(_shown) : text);
            _shown = text.Length;
        }

        public void End()
        {
            if (_shown > 0)
            {
                writer.WriteLine();
                _shown = 0;
            }
        }
    }
}

internal static class UpdateText
{
    public static void WriteCheck(TextWriter writer, UpdateService updater, UpdateCheck check)
    {
        string mode = updater.Install.IsInstalled ? "installed" : "portable";
        writer.WriteLine($"Niskala Utilities {check.Current} ({mode}, {updater.Install.Root})");
        string published = check.Published is { } date ? $", published {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : string.Empty;
        writer.WriteLine(check.Status switch
        {
            UpdateStatus.Available => $"Latest release: {check.Latest}{published}: an update is available.",
            UpdateStatus.UpToDate => $"Latest release: {check.Latest}{published}: this copy is up to date.",
            _ => "No release is published on GitHub yet.",
        });

        if (check.IsAvailable && check.Page is { } page)
        {
            writer.WriteLine(page.AbsoluteUri);
        }
    }

    public static string PackageKind(InstallMode mode) =>
        mode == InstallMode.Installed ? "installer" : "portable command-line package";
}
