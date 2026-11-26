using Niskala.Core.Safety;

namespace Niskala.Scanner.Inventory.Msi;

public static class MsiLocationInference
{
    public static string? Infer(IEnumerable<string> componentPaths, PathGuard guard)
    {
        ArgumentNullException.ThrowIfNull(componentPaths);
        ArgumentNullException.ThrowIfNull(guard);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in componentPaths)
        {
            string? directory = path.EndsWith('\\') ? path.TrimEnd('\\') : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && PathSyntax.TryNormalize(directory, out string normalized) == PathVerdict.Allowed)
            {
                counts[normalized] = counts.GetValueOrDefault(normalized) + 1;
            }
        }

        List<KeyValuePair<string, int>> allowed = [.. counts.Where(pair => guard.Check(pair.Key).IsAllowed)];
        if (allowed.Count == 0)
        {
            return null;
        }

        string common = allowed.Skip(1).Aggregate(allowed[0].Key, (ancestor, pair) => CommonAncestor(ancestor, pair.Key));
        return common.Length > 0 && guard.Check(common).IsAllowed
            ? common
            : allowed.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.Length).First().Key;
    }

    internal static string CommonAncestor(string a, string b)
    {
        string[] left = a.Split('\\');
        string[] right = b.Split('\\');
        int shared = 0;
        while (shared < left.Length && shared < right.Length
            && string.Equals(left[shared], right[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        if (shared == 0)
        {
            return string.Empty;
        }

        string result = string.Join('\\', left, 0, shared);
        return shared == 1 ? result + "\\" : result;
    }
}
