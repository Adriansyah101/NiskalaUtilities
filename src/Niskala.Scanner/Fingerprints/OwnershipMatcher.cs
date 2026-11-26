using Niskala.Core.Commands;
using Niskala.Core.Safety;

namespace Niskala.Scanner.Fingerprints;

public sealed class OwnershipMatcher(IReadOnlyList<string> roots, ICommandEnvironment environment)
{
    public bool OwnsPath(string? path)
    {
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string text = environment.ExpandVariables(path.Trim().Trim('"').Trim());
        return PathSyntax.TryNormalize(text, out string normalized) == PathVerdict.Allowed
            && roots.Any(root => PathSyntax.IsSameOrUnder(normalized, root, out _));
    }

    public bool OwnsCommand(string? commandLine)
    {
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return OwnsPath(CommandLineParser.Parse(commandLine, environment).Executable) || Mentions(commandLine);
    }

    public bool Mentions(string? text)
    {
        if (roots.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string expanded = environment.ExpandVariables(text);
        foreach (string root in roots)
        {
            for (int index = expanded.IndexOf(root, StringComparison.OrdinalIgnoreCase);
                 index >= 0;
                 index = expanded.IndexOf(root, index + 1, StringComparison.OrdinalIgnoreCase))
            {
                int end = index + root.Length;
                bool startsClean = index == 0 || !char.IsLetterOrDigit(expanded[index - 1]);
                bool endsClean = end == expanded.Length || expanded[end] is '\\' or '/' or '"' or '\'' or ';' or ',';
                if (startsClean && endsClean)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
