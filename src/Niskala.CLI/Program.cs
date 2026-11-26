using System.CommandLine;
using System.Text;
using Niskala.Cli.Commands;

Console.OutputEncoding = Encoding.UTF8;

var verbose = new Option<bool>("--verbose", "-v")
{
    Description = "Also write log messages to stderr.",
    Recursive = true,
};

var root = new RootCommand("Niskala Utilities: uninstall, clean, optimize, repair and maintain Windows.");
root.Options.Add(verbose);
root.Subcommands.Add(ListCommand.Create(verbose));
root.Subcommands.Add(SizeCommand.Create(verbose));
root.Subcommands.Add(IconsCommand.Create(verbose));
root.Subcommands.Add(InspectCommand.Create(verbose));
root.Subcommands.Add(UninstallCommand.Create(verbose));
root.Subcommands.Add(LeftoversCommand.Create(verbose));
root.Subcommands.Add(CleanCommand.Create(verbose));
root.Subcommands.Add(RestoreCommand.Create(verbose));
root.Subcommands.Add(ForceCommand.Create(verbose));
root.Subcommands.Add(TrackCommand.Create(verbose));
root.Subcommands.Add(LogsCommand.Create(verbose));
root.Subcommands.Add(RepairCommand.Create(verbose));
root.Subcommands.Add(OptimizeCommand.Create(verbose));
root.Subcommands.Add(ToolkitCommand.Create(verbose));

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
