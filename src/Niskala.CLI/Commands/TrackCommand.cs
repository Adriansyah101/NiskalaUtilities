using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Niskala.Host;
using Niskala.Tracker;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Tracing;

namespace Niskala.Cli.Commands;

internal sealed record TrackOutput(int SchemaVersion, DateTimeOffset GeneratedAt, string File, InstallLog Log);

internal static class TrackCommand
{
    private const int SchemaVersion = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorBadFormat = 193;

    public static Command Create(Option<bool> verbose)
    {
        var installer = new Argument<string>("installer") { Description = "The .exe or .msi installer to run." };
        var arguments = new Option<string?>("--args") { Description = "Arguments for the installer, as one quoted string." };
        var name = new Option<string?>("--name") { Description = "Name the log is listed under; by default the installer's product name." };
        var json = new Option<bool>("--json") { Description = "Write the log as JSON to stdout; progress goes to stderr." };

        var command = new Command("track", "Run an installer and record what it changes, so niskala logs rollback can undo it exactly. Needs elevation.")
        {
            installer, arguments, name, json,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            string path = Path.GetFullPath(parse.GetValue(installer)!);
            string? installerArguments = parse.GetValue(arguments);
            string logName = parse.GetValue(name) ?? ProductName(path);
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("track", parse.GetValue(verbose), async (host, token) =>
            {
                if (!Environment.IsPrivilegedProcess)
                {
                    await Console.Error.WriteLineAsync("Recording an install needs elevation: run Niskala from an elevated prompt.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                TextWriter o = asJson ? Console.Error : Console.Out;
                o.WriteLine($"Recording {logName}: {path}");
                o.WriteLine(Console.IsInputRedirected
                    ? "Recording ends when setup and every process it started have exited."
                    : "Recording ends when setup and every process it started have exited; press Enter to end it sooner, Ctrl+C to abandon it.");

                using var finish = new CancellationTokenSource();
                if (!Console.IsInputRedirected)
                {
                    _ = Task.Run(async () =>
                    {
                        await Console.In.ReadLineAsync(token).ConfigureAwait(false);
                        await finish.CancelAsync().ConfigureAwait(false);
                    }, token);
                }

                InstallLog log;
                try
                {
                    log = await host.CreateInstallTracker().RunAsync(new TrackRequest(path, installerArguments, logName), new ConsoleProgress(Console.Error), finish.Token, token).ConfigureAwait(false);
                }
                catch (TrackerUnavailableException ex)
                {
                    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                    return ex.Error is ErrorAccessDenied or ErrorFileNotFound or ErrorBadFormat ? ExitCodes.Usage : ExitCodes.Failure;
                }
                catch (OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync("Recording abandoned; nothing was saved. Setup keeps running.").ConfigureAwait(false);
                    return ExitCodes.Cancelled;
                }

                string file = host.CreateInstallLogStore().PathOf(log.Id);
                LogText.WriteSummary(o, log, file);
                o.WriteLine($"Undo it: niskala logs rollback {log.Id}");
                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new TrackOutput(SchemaVersion, DateTimeOffset.Now, file, log), CliJson.Output.TrackOutput));
                }

                return ExitCodes.Success;
            }, cancellationToken);
        });

        return command;
    }

    private static string ProductName(string path)
    {
        string fallback = Path.GetFileNameWithoutExtension(path);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        string? product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
        return string.IsNullOrEmpty(product) ? fallback : product;
    }

    private sealed class ConsoleProgress(TextWriter writer) : IProgress<TrackProgress>
    {
        public void Report(TrackProgress value)
        {
            string? line = value switch
            {
                { Process: { ExitedAt: null } p } => $"  + {p.Id} {Path.GetFileName(p.ImagePath)}{(p.Adopted ? "  [Windows Installer service]" : string.Empty)}",
                { Process: { } p } => $"  - {p.Id} {Path.GetFileName(p.ImagePath)}",
                { Stage: TrackStage.Settling } => "Setup finished; collecting the last events...",
                { Stage: TrackStage.Reconciling } => "Comparing with the machine's state now...",
                _ => null,
            };

            if (line is not null)
            {
                writer.WriteLine(line);
            }
        }
    }
}
