namespace Niskala.Repair;

public sealed class RepairInvocation
{
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        RepairCommandCatalog.Dism,
        RepairCommandCatalog.Sfc,
        RepairCommandCatalog.Netsh,
        RepairCommandCatalog.IpConfig,
        RepairCommandCatalog.Chkdsk,
    };

    private static readonly HashSet<string> Tokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Online",
        "/Cleanup-Image",
        "/CheckHealth",
        "/RestoreHealth",
        "/StartComponentCleanup",
        "/scannow",
        "winsock",
        "int",
        "ip",
        "reset",
        "/flushdns",
        "/scan",
    };

    private static readonly HashSet<string> Destructive = new(StringComparer.OrdinalIgnoreCase)
    {
        "/ResetBase",
        "/RevertPendingActions",
        "/f",
        "/r",
        "/x",
        "/b",
        "/spotfix",
        "/offlinescanandfix",
        "/sdcleanup",
    };

    internal RepairInvocation(string systemDirectory, string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Path.IsPathFullyQualified(systemDirectory))
        {
            throw new ArgumentException($"{systemDirectory} is not a full path.", nameof(systemDirectory));
        }

        if (!Executables.Contains(executable))
        {
            throw new ArgumentException($"{executable} is not a repair program.", nameof(executable));
        }

        foreach (string argument in arguments)
        {
            if (IsDestructive(argument))
            {
                throw new ArgumentException($"{argument} changes data beyond a non-destructive repair.", nameof(arguments));
            }

            if (!Tokens.Contains(argument) && !IsDriveToken(argument) && !RepairSource.IsArgument(argument))
            {
                throw new ArgumentException($"{argument} is not a repair argument.", nameof(arguments));
            }
        }

        FileName = Path.Combine(Path.TrimEndingDirectorySeparator(systemDirectory), executable);
        Executable = executable;
        Arguments = [.. arguments];
    }

    public string FileName { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string CommandLine => $"{Executable} {string.Join(' ', Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a))}";

    internal static bool IsDestructive(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        int value = argument.IndexOf(':', StringComparison.Ordinal);
        string name = value > 1 ? argument[..value] : argument;
        return Destructive.Contains(name);
    }

    internal static bool IsDriveToken(string text) => text is [_, ':'] && char.IsAsciiLetter(text[0]);
}
