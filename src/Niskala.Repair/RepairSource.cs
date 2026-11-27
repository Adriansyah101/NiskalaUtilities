using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Niskala.Repair;

public enum RepairSourceKind
{
    Wim,
    Esd,
}

public enum RepairSourceProblem
{
    None,
    NotFullPath,
    WrongExtension,
    Missing,
    Link,
    UnsafeCharacters,
}

public sealed record RepairSource
{
    public const int DefaultIndex = 1;

    private const string Prefix = "/Source:";
    private const string LimitAccess = "/LimitAccess";

    private static readonly char[] Unsafe = ['"', '*', '?', '<', '>', '|', '\0'];

    private RepairSource(RepairSourceKind kind, string path, int index)
    {
        Kind = kind;
        Path = path;
        Index = index;
    }

    public RepairSourceKind Kind { get; }

    public string Path { get; }

    public int Index { get; }

    public string Argument => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Tag(Kind)}:{Path}:{Index}");

    public static string OfflineSwitch => LimitAccess;

    public static bool TryCreate(string? path, [NotNullWhen(true)] out RepairSource? source, out RepairSourceProblem problem) =>
        TryCreate(path, DefaultIndex, File.GetAttributes, out source, out problem);

    internal static bool TryCreate(string? path, int index, Func<string, FileAttributes> attributes, [NotNullWhen(true)] out RepairSource? source, out RepairSourceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        source = null;
        string trimmed = path?.Trim() ?? string.Empty;
        problem = Check(trimmed, attributes, out RepairSourceKind kind);
        if (problem != RepairSourceProblem.None)
        {
            return false;
        }

        source = new RepairSource(kind, System.IO.Path.GetFullPath(trimmed), index);
        return true;
    }

    public static bool IsArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Equals(LimitAccess, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = argument[Prefix.Length..];
        int kindEnd = rest.IndexOf(':', StringComparison.Ordinal);
        int indexStart = rest.LastIndexOf(':');
        if (kindEnd < 0 || indexStart <= kindEnd)
        {
            return false;
        }

        string tag = rest[..kindEnd];
        string file = rest[(kindEnd + 1)..indexStart];
        string number = rest[(indexStart + 1)..];
        return (tag.Equals(Tag(RepairSourceKind.Wim), StringComparison.OrdinalIgnoreCase) || tag.Equals(Tag(RepairSourceKind.Esd), StringComparison.OrdinalIgnoreCase))
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 1
            && Shape(file, out RepairSourceKind kind) == RepairSourceProblem.None
            && Tag(kind).Equals(tag, StringComparison.OrdinalIgnoreCase);
    }

    private static RepairSourceProblem Check(string path, Func<string, FileAttributes> attributes, out RepairSourceKind kind)
    {
        RepairSourceProblem shape = Shape(path, out kind);
        if (shape != RepairSourceProblem.None)
        {
            return shape;
        }

        FileAttributes found;
        try
        {
            found = attributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RepairSourceProblem.Missing;
        }

        if ((found & FileAttributes.Directory) != 0)
        {
            return RepairSourceProblem.Missing;
        }

        return (found & FileAttributes.ReparsePoint) != 0 ? RepairSourceProblem.Link : RepairSourceProblem.None;
    }

    private static RepairSourceProblem Shape(string path, out RepairSourceKind kind)
    {
        kind = RepairSourceKind.Wim;
        if (path.Length == 0 || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return RepairSourceProblem.NotFullPath;
        }

        if (path.IndexOfAny(Unsafe) >= 0 || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl))
        {
            return RepairSourceProblem.UnsafeCharacters;
        }

        string extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".wim", StringComparison.OrdinalIgnoreCase))
        {
            kind = RepairSourceKind.Wim;
            return RepairSourceProblem.None;
        }

        if (extension.Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            kind = RepairSourceKind.Esd;
            return RepairSourceProblem.None;
        }

        return RepairSourceProblem.WrongExtension;
    }

    private static string Tag(RepairSourceKind kind) => kind == RepairSourceKind.Esd ? "ESD" : "WIM";
}
