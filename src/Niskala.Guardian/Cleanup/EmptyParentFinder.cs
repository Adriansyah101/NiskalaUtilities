using Niskala.Core.Safety;

namespace Niskala.Guardian.Cleanup;

public sealed class EmptyParentFinder(IEnumerable<string> roots, PathGuard guard)
{
    private readonly string[] _roots = [.. roots.Select(r => PathSyntax.TryNormalize(r, out string normalized) == PathVerdict.Allowed ? normalized : null).OfType<string>()];

    public IReadOnlyList<string> Find(IEnumerable<string> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var going = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in removed)
        {
            if (PathSyntax.TryNormalize(path, out string normalized) == PathVerdict.Allowed)
            {
                going.Add(normalized);
            }
        }

        var found = new List<string>();
        foreach (string path in going.ToList())
        {
            string? topmost = null;
            for (string? parent = Path.GetDirectoryName(path); parent is not null && IsBelowRoot(parent); parent = Path.GetDirectoryName(parent))
            {
                if (!IsEmptyWithout(parent, going))
                {
                    break;
                }

                going.Add(parent);
                topmost = parent;
            }

            if (topmost is not null && guard.Check(topmost).IsAllowed)
            {
                found.Add(topmost);
            }
        }

        return [.. found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !found.Any(o => !o.Equals(f, StringComparison.OrdinalIgnoreCase) && PathSyntax.IsSameOrUnder(f, o, out _)))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private bool IsBelowRoot(string folder) =>
        _roots.Any(root => PathSyntax.IsSameOrUnder(folder, root, out _) && !folder.Equals(root, StringComparison.OrdinalIgnoreCase));

    private static bool IsEmptyWithout(string folder, HashSet<string> going)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).All(going.Contains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
