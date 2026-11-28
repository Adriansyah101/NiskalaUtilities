using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public readonly record struct TableFolder(string Full, string Relative, bool IsProtected);

public static class FileTableScan
{
    public const string FileSystem = "NTFS";
    public const long RootRecord = 5;
    public const long FirstUserRecord = 16;

    public static bool Supports(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return string.Equals(volume.FileSystem, FileSystem, StringComparison.OrdinalIgnoreCase);
    }

    public static void SortByRecord(List<MftRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        records.Sort(static (a, b) => a.Number.CompareTo(b.Number));
    }
}

public sealed class FileTablePaths
{
    public const int DeepestFolder = 4096;

    private readonly Dictionary<long, (long Parent, string Name)> _folders = [];
    private readonly Dictionary<long, TableFolder?> _resolved = [];
    private readonly Stack<long> _chain = new();
    private readonly TableFolder _root;

    public FileTablePaths(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = new TableFolder(root, string.Empty, false);
    }

    public int Folders => _folders.Count;

    public static FileTablePaths From(string root, IEnumerable<MftRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var paths = new FileTablePaths(root);
        foreach (MftRecord record in records)
        {
            if (record.IsDirectory && !record.IsReparsePoint)
            {
                paths.AddFolder(record.Id, record.Parent, record.Name);
            }
        }

        return paths;
    }

    public void AddFolder(long id, long parent, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if ((id & MftRecord.RecordMask) != FileTableScan.RootRecord)
        {
            _folders.TryAdd(id, (parent, name));
        }
    }

    public TableFolder? Resolve(long folder)
    {
        _chain.Clear();
        long current = folder;
        TableFolder? known = null;
        while (true)
        {
            if ((current & MftRecord.RecordMask) == FileTableScan.RootRecord)
            {
                known = _root;
                break;
            }

            if (_resolved.TryGetValue(current, out known))
            {
                break;
            }

            if (_chain.Count >= DeepestFolder || _chain.Contains(current) || !_folders.ContainsKey(current))
            {
                known = null;
                break;
            }

            _chain.Push(current);
            current = _folders[current].Parent;
        }

        while (_chain.TryPop(out long id))
        {
            if (known is { } parent)
            {
                string name = _folders[id].Name;
                known = new TableFolder(
                    Path.Join(parent.Full, name),
                    parent.Relative.Length == 0 ? name : $"{parent.Relative}\\{name}",
                    parent.IsProtected || ProtectedFiles.IsProtectedName(name));
            }

            _resolved[id] = known;
        }

        return known;
    }
}
