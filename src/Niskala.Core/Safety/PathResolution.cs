namespace Niskala.Core.Safety;

public enum ResolutionStatus
{
    Resolved,
    MissingDrive,
    NetworkDrive,
    UnresolvedShortName,
    Unresolvable,
}

public readonly record struct PathResolution(
    ResolutionStatus Status,
    string? CanonicalPath,
    bool Exists,
    bool IsReparsePoint,
    bool IsNameSurrogate,
    int Win32Error)
{
    public static PathResolution Failed(ResolutionStatus status, int win32Error = 0) =>
        new(status, null, false, false, false, win32Error);
}

public interface IPathResolver
{
    PathResolution Resolve(string normalizedPath);
}
