using Niskala.Interop.Etw;
using Niskala.Interop.Registry;
using Niskala.Tracker.Logs;

namespace Niskala.Tracker.Recording;

public sealed record ReconciledChanges(IReadOnlyList<FileChange> Files, IReadOnlyList<KeyChange> Keys, IReadOnlyList<ValueChange> Values);

public static class ChangeReconciler
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ReconciledChanges Reconcile(IEnumerable<EtwEvent> events, ISystemState state)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(state);

        var files = new Dictionary<string, FileHistory>(PathComparer);
        var keys = new Dictionary<string, KeyHistory>(PathComparer);
        var values = new Dictionary<(string Key, string Name), ValueHistory>(ValueComparer.Instance);
        foreach (EtwEvent e in events.OrderBy(e => e.TimeUtc).ThenBy(e => Order(e.Kind)))
        {
            switch (e.Kind)
            {
                case EtwEventKind.FileCreated:
                    FileHistory created = History(files, e.Path);
                    created.Existed ??= false;
                    created.Changed |= created.Existed == true;
                    created.IsDirectory |= e.Flags.HasFlag(EtwEventFlags.Directory);
                    break;
                case EtwEventKind.FileWritten or EtwEventKind.FileDeleted:
                    FileHistory touched = History(files, e.Path);
                    touched.Existed ??= true;
                    touched.Changed = true;
                    break;
                case EtwEventKind.FileRenamed when e.Name is { Length: > 0 } target && !PathComparer.Equals(e.Path, target):
                    bool directory = false;
                    if (e.Path.Length > 0)
                    {
                        FileHistory source = History(files, e.Path);
                        source.Existed ??= true;
                        source.Changed = true;
                        directory = source.IsDirectory;
                    }

                    FileHistory renamed = History(files, target);
                    renamed.Existed ??= false;
                    renamed.Changed = true;
                    renamed.IsDirectory |= directory;
                    break;
                case EtwEventKind.KeyCreated:
                    History(keys, e.Path).Existed ??= false;
                    break;
                case EtwEventKind.KeyDeleted:
                    History(keys, e.Path).Existed ??= true;
                    break;
                case EtwEventKind.ValueSet:
                    ValueHistory set = History(values, e.Path, e.Name ?? string.Empty);
                    set.Prior ??= IsNewKey(keys, e.Path) ? Prior.Absent : PriorFrom(e);
                    break;
                case EtwEventKind.ValueDeleted:
                    ValueHistory deleted = History(values, e.Path, e.Name ?? string.Empty);
                    deleted.Prior ??= IsNewKey(keys, e.Path) ? Prior.Absent : Prior.Unknown;
                    break;
            }
        }

        List<FileChange> fileChanges = [.. files.Select(f => FileChangeOf(f.Key, f.Value, state.File(f.Key))).OfType<FileChange>()];
        List<KeyChange> keyChanges = [.. keys.Select(k => KeyChangeOf(k.Key, k.Value, state.KeyExists(k.Key))).OfType<KeyChange>()];
        string[] createdKeys = [.. keyChanges.Where(k => k.Change == ChangeKind.Created).Select(k => k.Path)];
        string[] deletedKeys = [.. keyChanges.Where(k => k.Change == ChangeKind.Deleted).Select(k => k.Path)];
        List<ValueChange> valueChanges = [.. values
            .Where(v => !IsUnderAny(v.Key.Key, createdKeys) && !IsUnderAny(v.Key.Key, deletedKeys))
            .Select(v => ValueChangeOf(v.Key.Key, v.Key.Name, v.Value.Prior!, state.Value(v.Key.Key, v.Key.Name)))
            .OfType<ValueChange>()];

        return new ReconciledChanges(
            [.. fileChanges.OrderBy(f => f.Path, PathComparer)],
            [.. keyChanges.OrderBy(k => k.Path, PathComparer)],
            [.. valueChanges.OrderBy(v => v.Key, PathComparer).ThenBy(v => v.Name, PathComparer)]);
    }

    public static bool IsSameOrUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '\\');

    private static bool IsUnderAny(string path, string[] roots) => roots.Any(r => IsSameOrUnder(path, r));

    private static int Order(EtwEventKind kind) => kind is EtwEventKind.FileCreated or EtwEventKind.KeyCreated ? 0 : 1;

    private static Prior PriorFrom(EtwEvent e) => e switch
    {
        { Flags: var f } when !f.HasFlag(EtwEventFlags.Previous) => Prior.Unknown,
        { PreviousType: 0, PreviousSize: 0 } => Prior.Absent,
        { PreviousData: { } data } when data.Length == e.PreviousSize => new Prior(true, e.PreviousType, data),
        _ => Prior.Unknown,
    };

    private static bool IsNewKey(Dictionary<string, KeyHistory> keys, string key)
    {
        for (string? current = key; current is not null; current = Parent(current))
        {
            if (keys.TryGetValue(current, out KeyHistory? history) && history.Existed == false)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Parent(string key)
    {
        int slash = key.LastIndexOf('\\');
        return slash > 0 ? key[..slash] : null;
    }

    private static FileChange? FileChangeOf(string path, FileHistory history, FileState now) => (history.Existed, now) switch
    {
        (false, FileState.Missing) => null,
        (false, _) => new FileChange(path, ChangeKind.Created, now == FileState.Directory),
        (_, FileState.Missing) => new FileChange(path, ChangeKind.Deleted, history.IsDirectory),
        _ when history.Changed => new FileChange(path, ChangeKind.Modified, now == FileState.Directory),
        _ => null,
    };

    private static KeyChange? KeyChangeOf(string path, KeyHistory history, bool exists) => (history.Existed, exists) switch
    {
        (false, true) => new KeyChange(path, ChangeKind.Created),
        (true, false) => new KeyChange(path, ChangeKind.Deleted),
        _ => null,
    };

    private static ValueChange? ValueChangeOf(string key, string name, Prior prior, RawRegistryValue? now)
    {
        if (prior.IsAbsent)
        {
            return now is null ? null : new ValueChange(key, name, ChangeKind.Created) { Kind = now.Kind, Data = now.Data };
        }

        if (prior.Known && now is not null && prior.Kind == now.Kind && prior.Data!.AsSpan().SequenceEqual(now.Data))
        {
            return null;
        }

        return new ValueChange(key, name, now is null ? ChangeKind.Deleted : ChangeKind.Modified)
        {
            Kind = now?.Kind,
            Data = now?.Data,
            PreviousKind = prior.Known ? prior.Kind : null,
            PreviousData = prior.Known ? prior.Data : null,
        };
    }

    private static FileHistory History(Dictionary<string, FileHistory> map, string path) =>
        map.TryGetValue(path, out FileHistory? history) ? history : map[path] = new FileHistory();

    private static KeyHistory History(Dictionary<string, KeyHistory> map, string path) =>
        map.TryGetValue(path, out KeyHistory? history) ? history : map[path] = new KeyHistory();

    private static ValueHistory History(Dictionary<(string, string), ValueHistory> map, string key, string name) =>
        map.TryGetValue((key, name), out ValueHistory? history) ? history : map[(key, name)] = new ValueHistory();

    private sealed class FileHistory
    {
        public bool? Existed { get; set; }

        public bool Changed { get; set; }

        public bool IsDirectory { get; set; }
    }

    private sealed class KeyHistory
    {
        public bool? Existed { get; set; }
    }

    private sealed class ValueHistory
    {
        public Prior? Prior { get; set; }
    }

    private sealed record Prior(bool Known, int Kind, byte[]? Data, bool IsAbsent = false)
    {
        public static Prior Absent { get; } = new(false, 0, null, IsAbsent: true);

        public static Prior Unknown { get; } = new(false, 0, null);
    }

    private sealed class ValueComparer : IEqualityComparer<(string Key, string Name)>
    {
        public static ValueComparer Instance { get; } = new();

        public bool Equals((string Key, string Name) x, (string Key, string Name) y) => PathComparer.Equals(x.Key, y.Key) && PathComparer.Equals(x.Name, y.Name);

        public int GetHashCode((string Key, string Name) obj) => HashCode.Combine(PathComparer.GetHashCode(obj.Key), PathComparer.GetHashCode(obj.Name));
    }
}
