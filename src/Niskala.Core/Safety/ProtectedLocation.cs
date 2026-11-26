namespace Niskala.Core.Safety;

public enum ProtectionKind
{
    Tree,
    Self,
}

public sealed record ProtectedLocation(string Path, ProtectionKind Kind, string Reason, string? ChildNamePrefix = null)
{
    public override string ToString() =>
        ChildNamePrefix is null ? $"{Kind} {Path} ({Reason})" : $"Tree {Path}\\{ChildNamePrefix}* ({Reason})";
}
