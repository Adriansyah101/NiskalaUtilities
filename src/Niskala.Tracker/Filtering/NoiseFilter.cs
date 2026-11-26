using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;

namespace Niskala.Tracker.Filtering;

public sealed record NoiseRule(string Pattern, string Reason)
{
    private readonly string[] _segments = Pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    public bool Matches(string path)
    {
        string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < _segments.Length)
        {
            return false;
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] != "*" && !string.Equals(_segments[i], segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record FilteredChanges(ReconciledChanges Changes, IReadOnlyDictionary<string, int> Filtered);

public sealed class NoiseFilter(IReadOnlyList<NoiseRule> fileRules, IReadOnlyList<NoiseRule> keyRules, PathGuard pathGuard, RegistryGuard registryGuard)
{
    private const string RevertOnly = "revert only";

    public static NoiseFilter ForCurrentMachine(PathGuard pathGuard, RegistryGuard registryGuard, ICommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new NoiseFilter(DefaultNoise.Files(environment), DefaultNoise.Keys, pathGuard, registryGuard);
    }

    public FilteredChanges Apply(ReconciledChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var filtered = new Dictionary<string, int>(StringComparer.Ordinal);

        var keptFolders = new List<string>();
        var droppedFolders = new List<(string Path, string Reason)>();
        var files = new List<FileChange>();
        foreach (FileChange file in changes.Files.OrderBy(f => f.Path.Length))
        {
            int dropped = droppedFolders.FindIndex(d => ChangeReconciler.IsSameOrUnder(file.Path, d.Path));
            if (dropped >= 0)
            {
                Count(filtered, droppedFolders[dropped].Reason);
                continue;
            }

            if (file.Change == ChangeKind.Created && keptFolders.Exists(k => ChangeReconciler.IsSameOrUnder(file.Path, k)))
            {
                files.Add(file);
                continue;
            }

            string? reason = FileNoise(file.Path);
            if (reason is not null)
            {
                Count(filtered, reason);
                if (file.IsDirectory)
                {
                    droppedFolders.Add((file.Path, reason));
                }

                continue;
            }

            if (file is { Change: ChangeKind.Created, IsDirectory: true })
            {
                keptFolders.Add(file.Path);
            }

            files.Add(file);
        }

        var keys = new List<KeyChange>();
        foreach (KeyChange key in changes.Keys)
        {
            string? reason = KeyNoise(key.Path, null);
            if (reason is null)
            {
                keys.Add(key);
            }
            else
            {
                Count(filtered, reason);
            }
        }

        var values = new List<ValueChange>();
        foreach (ValueChange value in changes.Values)
        {
            string? reason = KeyNoise(value.Key, value.Name);
            if (reason is null)
            {
                values.Add(value);
            }
            else if (reason == RevertOnly)
            {
                values.Add(value with { RevertOnly = true });
            }
            else
            {
                Count(filtered, reason);
            }
        }

        return new FilteredChanges(
            new ReconciledChanges([.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)], keys, values),
            filtered);
    }

    private string? FileNoise(string path)
    {
        if (fileRules.FirstOrDefault(r => r.Matches(path)) is { } rule)
        {
            return rule.Reason;
        }

        PathDecision decision = pathGuard.Check(path);
        return decision.IsAllowed ? null : $"Protected ({decision.Verdict})";
    }

    private string? KeyNoise(string key, string? valueName)
    {
        if (keyRules.FirstOrDefault(r => r.Matches(key)) is { } rule)
        {
            return rule.Reason;
        }

        RegistryDecision decision = valueName is null ? registryGuard.CheckKey(key) : registryGuard.CheckValue(key, valueName);
        return decision.IsAllowed ? null
            : valueName is not null && registryGuard.IsContentRevertible(key, valueName) ? RevertOnly
            : $"Protected ({decision.Verdict})";
    }

    private static void Count(Dictionary<string, int> filtered, string reason) =>
        filtered[reason] = filtered.GetValueOrDefault(reason) + 1;
}
