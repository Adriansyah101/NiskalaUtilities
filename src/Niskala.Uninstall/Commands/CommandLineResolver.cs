using System.Text.RegularExpressions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Uninstall.Security;

namespace Niskala.Uninstall.Commands;

public sealed partial class CommandLineResolver(ICommandEnvironment environment, IWritableLocationProbe locations)
{
    private static readonly string[] ShellHosts = ["cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe"];
    private static readonly string[] MsiexecSilent = ["/q", "/qn", "/qn-", "/qn+", "/quiet", "/passive"];
    private static readonly string[] BurnSilent = ["/quiet", "-quiet", "/q", "-q", "/passive", "-passive"];
    private static readonly string[] InnoSilent = ["/SILENT", "/VERYSILENT"];
    private static readonly string[] SquirrelSilent = ["-s", "--silent"];
    private static readonly string[] GenericSilent = ["/silent", "/verysilent", "/quiet", "/qn", "-silent", "--silent", "-q", "/q"];

    public CommandResolution Resolve(InstalledApp app, UninstallMode mode)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.InstallerKind == InstallerKind.Msix)
        {
            return new CommandResolution(mode, null, app.Package is null ? ResolutionIssue.NoCommand : ResolutionIssue.None, [], [])
            {
                PackageFullName = app.Package?.FullName,
            };
        }

        var notes = new List<string>();
        Draft draft = app.InstallerKind switch
        {
            InstallerKind.Msi when app.MsiProductCode is { } code => WindowsInstaller(app, code, mode, notes),
            InstallerKind.WingetPortable when Winget(app, mode, notes) is { } winget => winget,
            _ => Stored(app, mode, notes),
        };

        IReadOnlyList<CommandWarning> warnings = draft.Command is null ? [] : Warnings(app, draft.Command, draft.Source);
        return new CommandResolution(mode, draft.Command, draft.Issue, warnings, notes);
    }

    internal static bool LooksSilent(string arguments, InstallerKind kind, bool runsMsiexec)
    {
        string[] tokens = Tokens(arguments);
        if (runsMsiexec)
        {
            return tokens.Any(t => MsiexecSilent.Contains(t, StringComparer.OrdinalIgnoreCase));
        }

        return kind switch
        {
            InstallerKind.Nsis => tokens.Contains("/S", StringComparer.Ordinal),
            InstallerKind.InnoSetup => tokens.Any(t => InnoSilent.Contains(t, StringComparer.OrdinalIgnoreCase)),
            InstallerKind.WixBurn => tokens.Any(t => BurnSilent.Contains(t, StringComparer.OrdinalIgnoreCase)),
            InstallerKind.Squirrel => tokens.Any(t => SquirrelSilent.Contains(t, StringComparer.OrdinalIgnoreCase)),
            _ => tokens.Contains("/S", StringComparer.Ordinal) || tokens.Any(t => GenericSilent.Contains(t, StringComparer.OrdinalIgnoreCase)),
        };
    }

    internal static string[]? SilentSwitches(InstallerKind kind, bool runsMsiexec) => runsMsiexec
        ? ["/qn", "/norestart"]
        : kind switch
        {
            InstallerKind.Nsis => ["/S"],
            InstallerKind.InnoSetup => ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
            InstallerKind.WixBurn => ["/quiet", "/norestart"],
            InstallerKind.Squirrel => ["-s"],
            _ => null,
        };

    private Draft WindowsInstaller(InstalledApp app, Guid productCode, UninstallMode mode, List<string> notes)
    {
        string msiexec = Path.Combine(environment.SystemDirectory, "msiexec.exe");
        if (!environment.FileExists(msiexec))
        {
            notes.Add($"Executable not found: {msiexec}");
            return new Draft(null, ResolutionIssue.ExecutableMissing, null);
        }

        bool silent = mode == UninstallMode.Silent;
        string arguments = $"/x {productCode.ToString("B").ToUpperInvariant()}" + (silent ? " /qn /norestart" : string.Empty);
        if (app.UninstallString is { } stored && MsiInstallSwitch().IsMatch(stored))
        {
            notes.Add("The stored command uses /I, which opens maintenance mode; /x removes the product.");
        }

        return new Draft(new ResolvedCommand(msiexec, arguments, CommandOrigin.WindowsInstaller, silent), ResolutionIssue.None, null);
    }

    private Draft? Winget(InstalledApp app, UninstallMode mode, List<string> notes)
    {
        ParsedCommand? stored = app.UninstallCommand ?? app.QuietUninstallCommand;
        if (stored is not { Executable: { } name } || !name.Equals("winget", StringComparison.OrdinalIgnoreCase) || stored.Arguments.Length == 0)
        {
            return null;
        }

        string alias = environment.ExpandVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
        if (!environment.FileExists(alias))
        {
            notes.Add($"Executable not found: {alias}");
            return new Draft(null, ResolutionIssue.ExecutableMissing, stored);
        }

        string arguments = stored.Arguments;
        bool silent = mode == UninstallMode.Silent;
        if (silent)
        {
            arguments = Append(arguments, ["--silent", "--disable-interactivity"], StringComparer.OrdinalIgnoreCase, notes);
        }

        return new Draft(new ResolvedCommand(alias, arguments, CommandOrigin.Winget, silent), ResolutionIssue.None, stored);
    }

    private Draft Stored(InstalledApp app, UninstallMode mode, List<string> notes)
    {
        bool silent = mode == UninstallMode.Silent;
        (ParsedCommand? chosen, CommandOrigin origin) = (app.UninstallCommand, app.QuietUninstallCommand) switch
        {
            (_, { ExecutableExists: true } quiet) when silent => (quiet, CommandOrigin.QuietUninstallString),
            ({ } normal, _) => (normal, CommandOrigin.UninstallString),
            (null, { } quiet) => (quiet, CommandOrigin.QuietUninstallString),
            _ => ((ParsedCommand?)null, CommandOrigin.UninstallString),
        };

        if (chosen is null)
        {
            return new Draft(null, ResolutionIssue.NoCommand, null);
        }

        if (!chosen.ExecutableExists || chosen.Executable is null)
        {
            notes.Add($"Executable not found: {chosen.Executable ?? chosen.Raw}");
            return new Draft(null, ResolutionIssue.ExecutableMissing, chosen);
        }

        if (origin == CommandOrigin.QuietUninstallString && !silent)
        {
            notes.Add("Only a quiet uninstall command is registered.");
        }

        string executable = chosen.Executable;
        string arguments = chosen.Arguments;
        bool runsMsiexec = IsSystemBinary(executable, "msiexec.exe");
        if (runsMsiexec && MsiInstallSwitch().IsMatch(arguments))
        {
            arguments = MsiInstallSwitch().Replace(arguments, "/X");
            notes.Add("/I (maintenance mode) replaced by /X.");
        }

        if (app.InstallerKind == InstallerKind.WixBurn && !HasToken(arguments, "/uninstall") && !HasToken(arguments, "-uninstall"))
        {
            arguments = Append(arguments, ["/uninstall"], StringComparer.OrdinalIgnoreCase, notes);
        }

        bool isSilent = origin == CommandOrigin.QuietUninstallString || LooksSilent(arguments, app.InstallerKind, runsMsiexec);
        ResolutionIssue issue = ResolutionIssue.None;
        if (silent && !isSilent)
        {
            if (SilentSwitches(app.InstallerKind, runsMsiexec) is { } switches)
            {
                StringComparer comparer = app.InstallerKind == InstallerKind.Nsis ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                arguments = Append(arguments, switches, comparer, notes);
                isSilent = true;
            }
            else
            {
                issue = ResolutionIssue.SilentUnsupported;
            }
        }

        return new Draft(new ResolvedCommand(executable, arguments, origin, isSilent), issue, chosen);
    }

    private List<CommandWarning> Warnings(InstalledApp app, ResolvedCommand command, ParsedCommand? source)
    {
        var warnings = new List<CommandWarning>();
        if (source?.IsAmbiguous == true)
        {
            warnings.Add(new CommandWarning(
                CommandWarningKind.AmbiguousUnquotedPath,
                $"\"{source.Raw}\" is unquoted and several files match it; {command.ApplicationName} was chosen."));
        }

        string fileName = Path.GetFileName(command.ApplicationName);
        if (ShellHosts.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(new CommandWarning(CommandWarningKind.ShellInterpreter, $"The command runs {fileName}; what it executes is not checked."));
        }

        if (app.Scope == InstallScope.Machine && command.Origin != CommandOrigin.WindowsInstaller)
        {
            LocationCheck check = locations.Check(command.ApplicationName);
            if (check.Trust == LocationTrust.UserWritable)
            {
                warnings.Add(new CommandWarning(
                    CommandWarningKind.UserWritableLocation,
                    $"Registered machine-wide, but {check.Principal} can modify {check.Path}; running it elevated runs code that account controls."));
            }
            else if (check.Trust == LocationTrust.Unknown)
            {
                warnings.Add(new CommandWarning(CommandWarningKind.UnverifiedLocation, $"Permissions of {check.Path} could not be read."));
            }
        }

        return warnings;
    }

    private bool IsSystemBinary(string path, string fileName) =>
        string.Equals(path, Path.Combine(environment.SystemDirectory, fileName), StringComparison.OrdinalIgnoreCase);

    private static string Append(string arguments, string[] switches, StringComparer comparer, List<string> notes)
    {
        string[] tokens = Tokens(arguments);
        string[] missing = [.. switches.Where(s => !tokens.Contains(s, comparer))];
        if (missing.Length == 0)
        {
            return arguments;
        }

        notes.Add($"Added {string.Join(' ', missing)}.");
        return arguments.Length == 0 ? string.Join(' ', missing) : $"{arguments} {string.Join(' ', missing)}";
    }

    private static bool HasToken(string arguments, string token) => Tokens(arguments).Contains(token, StringComparer.OrdinalIgnoreCase);

    private static string[] Tokens(string arguments) => arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"(?<=^|\s)/[Ii](?=\s*\{|\s*$|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex MsiInstallSwitch();

    private readonly record struct Draft(ResolvedCommand? Command, ResolutionIssue Issue, ParsedCommand? Source);
}
