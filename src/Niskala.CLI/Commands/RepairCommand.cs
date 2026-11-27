using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Niskala.Repair;
using CatalogEntry = Niskala.Repair.RepairCommand;

namespace Niskala.Cli.Commands;

internal sealed record RepairListItem(string Name, RepairCommandId Id, string CommandLine, bool NeedsElevation, bool ChangesSystem, bool NeedsRestart, bool LongRunning, string Summary);

internal sealed record RepairListOutput(int SchemaVersion, IReadOnlyList<RepairListItem> Commands);

internal sealed record RepairRunOutput(
    int SchemaVersion,
    string Name,
    RepairCommandId Id,
    string CommandLine,
    RepairOutcome Outcome,
    RepairDiagnostic Diagnostic,
    int? ExitCode,
    int? StartError,
    double? LastPercent,
    long ElapsedMs);

internal static class RepairCommand
{
    private const int SchemaVersion = 1;

    public static Command Create(Option<bool> verbose) =>
        new("repair", "Run a non-destructive Windows repair from a fixed list: DISM, SFC, network resets, DNS flush, online disk scan.")
        {
            List(verbose), Run(verbose),
        };

    private static Command List(Option<bool> verbose)
    {
        var json = new Option<bool>("--json") { Description = "Write the list as JSON to stdout." };
        var command = new Command("list", "List the repairs niskala can run.") { json };
        command.SetAction((parse, cancellationToken) =>
        {
            bool asJson = parse.GetValue(json);
            return CommandRunner.RunAsync("repair list", parse.GetValue(verbose), (_, _) =>
            {
                List<RepairListItem> items = [.. RepairCommandCatalog.All.Select(c => new RepairListItem(
                    c.Name,
                    c.Id,
                    RepairCommandCatalog.Resolve(c.Id, SystemRepairEnvironment.Instance).CommandLine,
                    c.NeedsElevation,
                    c.ChangesSystem,
                    c.NeedsRestart,
                    c.IsLongRunning,
                    RepairText.Summary(c.Id)))];

                if (asJson)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new RepairListOutput(SchemaVersion, items), CliJson.Output.RepairListOutput));
                    return Task.FromResult(ExitCodes.Success);
                }

                foreach (RepairListItem item in items)
                {
                    Console.Out.WriteLine($"{item.Name,-24} {item.CommandLine}");
                    Console.Out.WriteLine($"{string.Empty,-24} {item.Summary}{RepairText.Traits(item)}");
                }

                if (!Environment.IsPrivilegedProcess)
                {
                    Console.Out.WriteLine();
                    Console.Out.WriteLine("Every repair needs an elevated prompt.");
                }

                return Task.FromResult(ExitCodes.Success);
            }, cancellationToken);
        });

        return command;
    }

    private static Command Run(Option<bool> verbose)
    {
        var name = new Argument<string>("command") { Description = "Repair name, as niskala repair list shows it." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask before a repair that changes Windows." };
        var json = new Option<bool>("--json") { Description = "Write the result as JSON to stdout; the tool's output goes to stderr." };
        var sourceOption = new Option<string?>("--source")
        {
            Description = "dism-restore-health only: repair from this install.wim or install.esd (image 1) instead of Windows Update.",
        };
        var command = new Command("run", "Run one repair and show its output and progress as it happens.") { name, yes, json, sourceOption };
        command.SetAction((parse, cancellationToken) =>
        {
            string requested = parse.GetValue(name)!;
            bool assumeYes = parse.GetValue(yes);
            bool asJson = parse.GetValue(json);
            string? sourcePath = parse.GetValue(sourceOption);
            return CommandRunner.RunAsync("repair run", parse.GetValue(verbose), async (host, token) =>
            {
                if (!RepairCommandCatalog.TryFind(requested, out CatalogEntry? repair))
                {
                    await Console.Error.WriteLineAsync($"{requested} is not a repair. niskala repair list shows them all.").ConfigureAwait(false);
                    return ExitCodes.Usage;
                }

                if (repair.NeedsElevation && !SystemRepairEnvironment.Instance.IsElevated)
                {
                    await Console.Error.WriteLineAsync($"{repair.Name} needs an elevated prompt; nothing was started.").ConfigureAwait(false);
                    return ExitCodes.ElevationRequired;
                }

                RepairSource? source = null;
                if (sourcePath is not null)
                {
                    if (!RepairCommandCatalog.AcceptsSource(repair.Id))
                    {
                        await Console.Error.WriteLineAsync($"{repair.Name} takes no --source.").ConfigureAwait(false);
                        return ExitCodes.Usage;
                    }

                    if (!RepairSource.TryCreate(sourcePath, out source, out RepairSourceProblem problem))
                    {
                        await Console.Error.WriteLineAsync($"{sourcePath} is not a usable repair source: {RepairText.SourceProblem(problem)}").ConfigureAwait(false);
                        return ExitCodes.Usage;
                    }
                }

                string commandLine = RepairCommandCatalog.Resolve(repair.Id, SystemRepairEnvironment.Instance, source).CommandLine;
                if (repair.ChangesSystem && await Confirmation.RequireAsync($"Run {commandLine}? {RepairText.Summary(repair.Id)}", assumeYes, token).ConfigureAwait(false) is { } declined)
                {
                    return declined;
                }

                TextWriter output = asJson ? Console.Error : Console.Out;
                var renderer = new LiveOutput(output, live: !(asJson ? Console.IsErrorRedirected : Console.IsOutputRedirected));
                RepairResult result = await host.CreateRepairRunner().RunAsync(repair.Id, source, renderer, token).ConfigureAwait(false);
                renderer.End();

                if (asJson)
                {
                    var report = new RepairRunOutput(
                        SchemaVersion, repair.Name, repair.Id, commandLine, result.Outcome, result.Diagnostic, result.ExitCode, result.StartError, result.LastPercent, (long)result.Elapsed.TotalMilliseconds);
                    Console.Out.WriteLine(JsonSerializer.Serialize(report, CliJson.Output.RepairRunOutput));
                }
                else
                {
                    await Console.Error.WriteLineAsync(RepairText.Result(repair, result)).ConfigureAwait(false);
                }

                return result.Outcome switch
                {
                    RepairOutcome.Succeeded => ExitCodes.Success,
                    RepairOutcome.RestartRequired => ExitCodes.RestartRequired,
                    RepairOutcome.NotElevated => ExitCodes.ElevationRequired,
                    RepairOutcome.Cancelled => ExitCodes.Cancelled,
                    _ => ExitCodes.Failure,
                };
            }, cancellationToken);
        });

        return command;
    }

    private sealed class LiveOutput(TextWriter writer, bool live) : IProgress<RepairUpdate>
    {
        private readonly Lock _gate = new();
        private int _shown;

        public void Report(RepairUpdate value)
        {
            lock (_gate)
            {
                if (value.IsFinal)
                {
                    Overwrite(value.Text);
                    writer.WriteLine();
                    _shown = 0;
                }
                else if (live)
                {
                    Overwrite(value.Text);
                    _shown = value.Text.Length;
                }
            }
        }

        public void End()
        {
            lock (_gate)
            {
                if (_shown > 0)
                {
                    writer.WriteLine();
                    _shown = 0;
                }

                writer.Flush();
            }
        }

        private void Overwrite(string text)
        {
            if (_shown > 0)
            {
                writer.Write('\r');
            }

            writer.Write(text.Length < _shown ? text.PadRight(_shown) : text);
        }
    }
}

internal static class RepairText
{
    public static string Summary(RepairCommandId id) => id switch
    {
        RepairCommandId.DismCheckHealth => "Reports whether the component store is marked corrupt; changes nothing.",
        RepairCommandId.DismRestoreHealth => "Repairs the component store from Windows Update.",
        RepairCommandId.SfcScanNow => "Checks protected system files and replaces damaged ones from the component store.",
        RepairCommandId.DismComponentCleanup => "Removes superseded components; installed updates can still be uninstalled.",
        RepairCommandId.NetshWinsockReset => "Resets the Winsock catalog to its default.",
        RepairCommandId.NetshIpReset => "Resets the TCP/IP stack settings to their default.",
        RepairCommandId.FlushDns => "Empties the DNS resolver cache.",
        RepairCommandId.ChkdskOnlineScan => "Scans the system volume while it stays online; fixes nothing offline.",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    public static string Traits(RepairListItem item)
    {
        var traits = new List<string>(3);
        if (item.ChangesSystem)
        {
            traits.Add("changes Windows");
        }

        if (item.NeedsRestart)
        {
            traits.Add("restart afterwards");
        }

        if (item.LongRunning)
        {
            traits.Add("can take a long time");
        }

        return traits.Count == 0 ? string.Empty : $" ({string.Join(", ", traits)})";
    }

    public static string Result(CatalogEntry command, RepairResult result)
    {
        string elapsed = result.Elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        string code = result.ExitCode is { } exit ? string.Create(CultureInfo.InvariantCulture, $", exit code {exit} (0x{exit:X8})") : string.Empty;
        string outcome = result.Outcome switch
        {
            RepairOutcome.Succeeded => "finished",
            RepairOutcome.RestartRequired => "finished; restart Windows to complete it",
            RepairOutcome.IssuesFound => "found problems",
            RepairOutcome.StartFailed => string.Create(CultureInfo.InvariantCulture, $"did not start (Win32 error {result.StartError})"),
            RepairOutcome.Cancelled => "was cancelled; its process tree was stopped",
            _ => "failed",
        };

        string diagnostic = Diagnostic(result.Diagnostic) is { } text ? $" {text}" : string.Empty;
        return $"{command.Name} {outcome} in {elapsed}{code}.{diagnostic}";
    }

    private static string? Diagnostic(RepairDiagnostic diagnostic) => diagnostic switch
    {
        RepairDiagnostic.ElevationRequired => "Run niskala from an elevated prompt.",
        RepairDiagnostic.AccessDenied => "Windows denied access to a setting the tool resets.",
        RepairDiagnostic.InvalidParameter => "This Windows version does not know one of the options.",
        RepairDiagnostic.SourceFilesMissing => "DISM found no repair source files (0x800F081F); check the internet connection and Windows Update, or pass --source with install.wim or install.esd from a Windows ISO.",
        RepairDiagnostic.SourceDownloadFailed => "DISM could not download the repair files from Windows Update; check the internet connection, or pass --source with install.wim or install.esd from a Windows ISO.",
        RepairDiagnostic.RepairSourceNotFound => "Windows Update could not supply the repair files (0x800F0915); check the internet connection and the Windows Update service, or pass --source with install.wim or install.esd from a Windows ISO.",
        RepairDiagnostic.BlockedByPolicy => "A Group Policy keeps DISM from using Windows Update as a repair source.",
        RepairDiagnostic.PendingOperations => "Windows has servicing operations pending; restart and run it again.",
        RepairDiagnostic.OfflineRepairNeeded => "The volume needs an offline repair; Windows offers it in the drive's properties under Tools.",
        RepairDiagnostic.ProgramMissing => "The program is missing from System32.",
        _ => null,
    };

    public static string SourceProblem(RepairSourceProblem problem) => problem switch
    {
        RepairSourceProblem.NotFullPath => "give the full path on a local drive.",
        RepairSourceProblem.WrongExtension => "only .wim and .esd images can repair Windows.",
        RepairSourceProblem.Link => "links are not followed.",
        RepairSourceProblem.UnsafeCharacters => "the path holds characters DISM cannot take.",
        _ => "the file was not found.",
    };
}
