namespace Niskala.Uninstall.Commands;

public enum UninstallMode
{
    Interactive,
    Silent,
}

public enum CommandOrigin
{
    UninstallString,
    QuietUninstallString,
    WindowsInstaller,
    Winget,
}

public enum ResolutionIssue
{
    None,
    NoCommand,
    ExecutableMissing,
    SilentUnsupported,
}

public enum CommandWarningKind
{
    UserWritableLocation,
    UnverifiedLocation,
    AmbiguousUnquotedPath,
    ShellInterpreter,
}

public sealed record CommandWarning(CommandWarningKind Kind, string Message);

public sealed record ResolvedCommand(string ApplicationName, string Arguments, CommandOrigin Origin, bool IsSilent)
{
    public string CommandLine => Arguments.Length == 0 ? $"\"{ApplicationName}\"" : $"\"{ApplicationName}\" {Arguments}";
}

public sealed record CommandResolution(
    UninstallMode Mode,
    ResolvedCommand? Command,
    ResolutionIssue Issue,
    IReadOnlyList<CommandWarning> Warnings,
    IReadOnlyList<string> Notes)
{
    public string? PackageFullName { get; init; }

    public bool CanRun => Command is not null || PackageFullName is not null;
}
