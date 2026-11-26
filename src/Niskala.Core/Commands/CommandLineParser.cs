namespace Niskala.Core.Commands;

public enum CommandParseIssue
{
    None,
    Empty,
    UnterminatedQuote,
    ExecutableNotFound,
}

public sealed record ParsedCommand(
    string Raw,
    string? Executable,
    string Arguments,
    bool ExecutableExists,
    CommandParseIssue Issue,
    bool IsAmbiguous = false);

public interface ICommandEnvironment
{
    bool FileExists(string path);

    string SystemDirectory { get; }

    string WindowsDirectory { get; }

    string ExpandVariables(string text);
}

public sealed class SystemCommandEnvironment : ICommandEnvironment
{
    public static SystemCommandEnvironment Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public string SystemDirectory => Environment.SystemDirectory;

    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string ExpandVariables(string text) => Environment.ExpandEnvironmentVariables(text);
}

public static class CommandLineParser
{
    public static ParsedCommand Parse(string? raw, ICommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ParsedCommand(raw ?? string.Empty, null, string.Empty, false, CommandParseIssue.Empty);
        }

        string text = environment.ExpandVariables(raw).Trim();
        if (!text.StartsWith('"'))
        {
            return ParseUnquoted(raw, text, environment);
        }

        int close = text.IndexOf('"', 1);
        if (close < 0)
        {
            string unterminated = text[1..].Trim();
            string? qualified = Qualify(unterminated, environment);
            return new ParsedCommand(raw, qualified ?? unterminated, string.Empty, qualified is not null, CommandParseIssue.UnterminatedQuote);
        }

        string quoted = text[1..close];
        string arguments = text[(close + 1)..].Trim();
        string? resolved = Qualify(quoted, environment);
        return resolved is null
            ? new ParsedCommand(raw, quoted, arguments, false, CommandParseIssue.ExecutableNotFound)
            : new ParsedCommand(raw, resolved, arguments, true, CommandParseIssue.None);
    }

    private static ParsedCommand ParseUnquoted(string raw, string text, ICommandEnvironment environment)
    {
        (int End, string Path)? best = null;
        int bestTier = int.MaxValue;
        int matches = 0;

        for (int end = 0; end <= text.Length; end++)
        {
            if (end < text.Length && text[end] != ' ')
            {
                continue;
            }

            string candidate = text[..end].TrimEnd();
            if (candidate.Length == 0)
            {
                continue;
            }

            int tier;
            string? path = Qualify(candidate, environment, appendExe: false);
            if (path is not null)
            {
                tier = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }
            else if (!Path.HasExtension(candidate) && (path = Qualify(candidate + ".exe", environment, appendExe: false)) is not null)
            {
                tier = 2;
            }
            else
            {
                continue;
            }

            matches++;
            if (tier < bestTier)
            {
                best = (end, path);
                bestTier = tier;
            }
        }

        if (best is { } found)
        {
            string arguments = found.End >= text.Length ? string.Empty : text[found.End..].Trim();
            return new ParsedCommand(raw, found.Path, arguments, true, CommandParseIssue.None, IsAmbiguous: matches > 1);
        }

        int space = text.IndexOf(' ');
        string first = space < 0 ? text : text[..space];
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        return new ParsedCommand(raw, first, rest, false, CommandParseIssue.ExecutableNotFound);
    }

    private static string? Qualify(string candidate, ICommandEnvironment environment, bool appendExe = true)
    {
        if (candidate.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(candidate))
        {
            if (environment.FileExists(candidate))
            {
                return candidate;
            }

            return appendExe && !Path.HasExtension(candidate) && environment.FileExists(candidate + ".exe") ? candidate + ".exe" : null;
        }

        if (candidate.Contains('\\') || candidate.Contains('/') || candidate.Contains(':'))
        {
            return null;
        }

        foreach (string directory in (ReadOnlySpan<string>)[environment.SystemDirectory, environment.WindowsDirectory])
        {
            string full = Path.Combine(directory, candidate);
            if (environment.FileExists(full))
            {
                return full;
            }

            if (!Path.HasExtension(candidate) && environment.FileExists(full + ".exe"))
            {
                return full + ".exe";
            }
        }

        return null;
    }
}
