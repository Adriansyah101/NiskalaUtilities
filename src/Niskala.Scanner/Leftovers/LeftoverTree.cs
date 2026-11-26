using System.Buffers.Binary;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Microsoft.Win32;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Registry;

namespace Niskala.Scanner.Leftovers;

public sealed record LeftoverNode(LeftoverKind Kind, string Path, string? ValueName, string Name)
{
    public bool HasChildren { get; init; }

    public bool IsLink { get; init; }

    public int? Files { get; init; }

    public long? Bytes { get; init; }

    public DateTime? Modified { get; init; }

    public string? ValueType { get; init; }

    public string? Data { get; init; }
}

public sealed record LeftoverChildren(IReadOnlyList<LeftoverNode> Nodes, bool Truncated, string? Error);

public static class LeftoverTree
{
    public const int MaxChildren = 5_000;
    private const int MaxText = 512;
    private const int MaxBinary = 64;

    private static readonly string[] TypeNames =
    [
        "REG_NONE", "REG_SZ", "REG_EXPAND_SZ", "REG_BINARY", "REG_DWORD", "REG_DWORD_BIG_ENDIAN", "REG_LINK", "REG_MULTI_SZ",
        "REG_RESOURCE_LIST", "REG_FULL_RESOURCE_DESCRIPTOR", "REG_RESOURCE_REQUIREMENTS_LIST", "REG_QWORD",
    ];

    private static readonly EnumerationOptions ChildOptions = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    public static LeftoverNode Root(LeftoverItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind switch
        {
            LeftoverKind.Folder => Folder(item.Path),
            LeftoverKind.File => File(item.Path),
            LeftoverKind.RegistryKey => Key(item.Path),
            LeftoverKind.RegistryValue => Value(item.Path, item.ValueName ?? string.Empty),
            _ => new LeftoverNode(item.Kind, item.Path, null, item.Path),
        };
    }

    public static LeftoverChildren Children(LeftoverNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.HasChildren || node.IsLink)
        {
            return new LeftoverChildren([], false, null);
        }

        try
        {
            return node.Kind switch
            {
                LeftoverKind.Folder => FolderChildren(node.Path),
                LeftoverKind.RegistryKey => KeyChildren(node.Path),
                _ => new LeftoverChildren([], false, null),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new LeftoverChildren([], false, ex.Message);
        }
    }

    public static bool? Combine(IEnumerable<bool?> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        bool any = false;
        bool all = true;
        bool empty = true;
        foreach (bool? state in children)
        {
            empty = false;
            any |= state != false;
            all &= state == true;
        }

        return empty ? false : all ? true : any ? null : false;
    }

    public static LeftoverItem ToItem(LeftoverItem leftover, LeftoverNode node)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        ArgumentNullException.ThrowIfNull(node);
        return new LeftoverItem(node.Kind, node.Path, node.ValueName, leftover.Score, leftover.Tier, leftover.Reasons)
        {
            Files = node.Kind == LeftoverKind.File ? 1 : node.Files,
            Bytes = node.Bytes,
        };
    }

    public static bool Contains(LeftoverItem leftover, LeftoverItem part)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        ArgumentNullException.ThrowIfNull(part);
        if (leftover.Kind == part.Kind
            && leftover.Path.Equals(part.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftover.ValueName, part.ValueName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (leftover.Kind, part.Kind) switch
        {
            (LeftoverKind.Folder, LeftoverKind.Folder or LeftoverKind.File) => PathSyntax.IsSameOrUnder(part.Path, leftover.Path, out _),
            (LeftoverKind.RegistryKey, LeftoverKind.RegistryKey) => IsKeyUnder(part.Path, leftover.Path, allowSame: false),
            (LeftoverKind.RegistryKey, LeftoverKind.RegistryValue) => IsKeyUnder(part.Path, leftover.Path, allowSame: true),
            _ => false,
        };
    }

    private static bool IsKeyUnder(string key, string parent, bool allowSame) =>
        (allowSame && key.Equals(parent, StringComparison.OrdinalIgnoreCase))
        || (key.Length > parent.Length + 1 && key[parent.Length] == '\\' && key.StartsWith(parent, StringComparison.OrdinalIgnoreCase));

    private static LeftoverNode Folder(string path)
    {
        var info = new DirectoryInfo(path);
        bool link = info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        bool children = info.Exists && !link && info.EnumerateFileSystemInfos("*", ChildOptions).Any();
        return new LeftoverNode(LeftoverKind.Folder, path, null, Name(path))
        {
            HasChildren = children,
            IsLink = link,
            Modified = info.Exists ? info.LastWriteTimeUtc : null,
        };
    }

    private static LeftoverNode File(string path)
    {
        var info = new FileInfo(path);
        return new LeftoverNode(LeftoverKind.File, path, null, Name(path))
        {
            Bytes = info.Exists ? info.Length : null,
            Modified = info.Exists ? info.LastWriteTimeUtc : null,
            IsLink = info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint),
        };
    }

    private static LeftoverChildren FolderChildren(string folder)
    {
        var entries = new FileSystemEnumerable<LeftoverNode>(
            folder,
            static (ref FileSystemEntry entry) =>
            {
                string path = entry.ToFullPath();
                bool link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                return entry.IsDirectory
                    ? new LeftoverNode(LeftoverKind.Folder, path, null, entry.FileName.ToString()) { HasChildren = !link, IsLink = link, Modified = entry.LastWriteTimeUtc.UtcDateTime }
                    : new LeftoverNode(LeftoverKind.File, path, null, entry.FileName.ToString()) { IsLink = link, Bytes = entry.Length, Modified = entry.LastWriteTimeUtc.UtcDateTime };
            },
            ChildOptions);
        List<LeftoverNode> nodes = [.. entries.Take(MaxChildren + 1)];
        bool truncated = nodes.Count > MaxChildren;
        return new LeftoverChildren(
            [.. nodes.Take(MaxChildren).Select(Measured).OrderBy(n => n.Kind != LeftoverKind.Folder).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)],
            truncated,
            null);
    }

    private static LeftoverNode Measured(LeftoverNode node)
    {
        if (node.Kind != LeftoverKind.Folder || node.IsLink)
        {
            return node;
        }

        (int files, long bytes) = FileLeftoverProbe.Measure(node.Path);
        return node with { Files = files, Bytes = bytes, HasChildren = files > 0 || new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", ChildOptions).Any() };
    }

    private static LeftoverNode Key(string path)
    {
        using RegistryKey? key = Open(path);
        return new LeftoverNode(LeftoverKind.RegistryKey, path, null, Name(path))
        {
            HasChildren = key is not null && (key.SubKeyCount > 0 || key.ValueCount > 0),
            Modified = key is not null && RegistryNative.TryGetLastWriteTime(key.Handle, out DateTime written) == 0 ? written : null,
        };
    }

    private static LeftoverNode Value(string keyPath, string name)
    {
        using RegistryKey? key = Open(keyPath);
        RawRegistryValue? value = null;
        if (key is not null)
        {
            RegistryNative.TryReadValue(key.Handle, name, out value);
        }

        return value is null ? new LeftoverNode(LeftoverKind.RegistryValue, keyPath, name, name) : ValueNode(keyPath, value);
    }

    private static LeftoverChildren KeyChildren(string path)
    {
        using RegistryKey? key = Open(path);
        if (key is null)
        {
            return new LeftoverChildren([], false, "the key is gone");
        }

        List<LeftoverNode> nodes = [];
        int error = RegistryNative.TryListSubKeys(key.Handle, out List<string> names);
        foreach (string name in names.Order(StringComparer.OrdinalIgnoreCase).Take(MaxChildren))
        {
            nodes.Add(Key($@"{path}\{name}"));
        }

        int valueError = RegistryNative.TryReadValues(key.Handle, out List<RawRegistryValue> values);
        nodes.AddRange(values
            .OrderBy(v => v.Name.Length > 0)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxChildren - nodes.Count)
            .Select(v => ValueNode(path, v)));
        bool truncated = names.Count + values.Count > MaxChildren;
        string? problem = (error | valueError) == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"Win32 error {(error != 0 ? error : valueError)}");
        return new LeftoverChildren(nodes, truncated, problem);
    }

    private static LeftoverNode ValueNode(string keyPath, RawRegistryValue value) =>
        new(LeftoverKind.RegistryValue, keyPath, value.Name, value.Name)
        {
            ValueType = value.Kind >= 0 && value.Kind < TypeNames.Length ? TypeNames[value.Kind] : string.Create(CultureInfo.InvariantCulture, $"type {value.Kind}"),
            Data = Format(value.Kind, value.Data),
            Bytes = value.Data.Length,
        };

    public static string Format(int kind, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return kind switch
        {
            1 or 2 or 6 => Shorten(Text(data).TrimEnd('\0')),
            7 => Shorten(string.Join(" | ", Text(data).Split('\0', StringSplitOptions.RemoveEmptyEntries))),
            4 when data.Length >= 4 => Number(BinaryPrimitives.ReadUInt32LittleEndian(data), "X8"),
            5 when data.Length >= 4 => Number(BinaryPrimitives.ReadUInt32BigEndian(data), "X8"),
            11 when data.Length >= 8 => Number(BinaryPrimitives.ReadUInt64LittleEndian(data), "X16"),
            _ => Hex(data),
        };
    }

    private static string Text(byte[] data) => Encoding.Unicode.GetString(data, 0, data.Length & ~1);

    private static string Number(ulong value, string format) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value.ToString(format, CultureInfo.InvariantCulture)} ({value})");

    private static string Hex(byte[] data)
    {
        string bytes = Convert.ToHexString(data, 0, Math.Min(data.Length, MaxBinary));
        var text = new StringBuilder(bytes.Length + (bytes.Length / 2) + 1);
        for (int i = 0; i < bytes.Length; i += 2)
        {
            text.Append(i == 0 ? string.Empty : " ").Append(bytes, i, 2);
        }

        return data.Length > MaxBinary ? text.Append(" …").ToString() : text.ToString();
    }

    private static string Shorten(string text) => text.Length > MaxText ? string.Concat(text.AsSpan(0, MaxText), "…") : text;

    private static string Name(string path) => path[(path.TrimEnd('\\').LastIndexOf('\\') + 1)..];

    private static RegistryKey? Open(string path)
    {
        try
        {
            return RegistryPath.OpenReadOnly(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
