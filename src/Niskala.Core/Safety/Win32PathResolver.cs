using Niskala.Interop.FileSystem;

namespace Niskala.Core.Safety;

public sealed class Win32PathResolver : IPathResolver
{
    public static Win32PathResolver Instance { get; } = new();

    public PathResolution Resolve(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedPath);

        string root = normalizedPath[..3];
        switch (FileSystemNative.GetDriveKind(root))
        {
            case DriveKind.NoRootDirectory:
            case DriveKind.Unknown:
                return PathResolution.Failed(ResolutionStatus.MissingDrive);
            case DriveKind.Remote:
                return PathResolution.Failed(ResolutionStatus.NetworkDrive);
        }

        int error = FileSystemNative.TryQueryIdentity(normalizedPath, openReparsePoint: true, out FileIdentity identity);
        if (error == FileSystemNative.ErrorSuccess)
        {
            ResolutionStatus status = ToCanonical(identity.FinalPath, out string? canonical);
            return status == ResolutionStatus.Resolved
                ? new PathResolution(status, canonical, true, identity.IsReparsePoint, identity.IsNameSurrogate, 0)
                : PathResolution.Failed(status);
        }

        if (!IsNotFound(error))
        {
            return PathResolution.Failed(ResolutionStatus.Unresolvable, error);
        }

        string[] segments = PathSyntax.GetSegments(normalizedPath);
        for (int keep = segments.Length - 1; keep >= 0; keep--)
        {
            string ancestor = root + string.Join('\\', segments, 0, keep);
            error = FileSystemNative.TryQueryIdentity(ancestor, openReparsePoint: false, out FileIdentity ancestorIdentity);
            if (error == FileSystemNative.ErrorSuccess)
            {
                for (int i = keep; i < segments.Length; i++)
                {
                    if (segments[i].Contains('~'))
                    {
                        return PathResolution.Failed(ResolutionStatus.UnresolvedShortName);
                    }
                }

                ResolutionStatus status = ToCanonical(ancestorIdentity.FinalPath, out string? canonicalAncestor);
                if (status != ResolutionStatus.Resolved)
                {
                    return PathResolution.Failed(status);
                }

                string tail = string.Join('\\', segments, keep, segments.Length - keep);
                string canonical = canonicalAncestor!.EndsWith('\\') ? canonicalAncestor + tail : canonicalAncestor + "\\" + tail;
                return new PathResolution(ResolutionStatus.Resolved, canonical, false, false, false, 0);
            }

            if (!IsNotFound(error))
            {
                return PathResolution.Failed(ResolutionStatus.Unresolvable, error);
            }
        }

        return PathResolution.Failed(ResolutionStatus.Unresolvable, error);
    }

    private static ResolutionStatus ToCanonical(string finalPath, out string? canonical)
    {
        canonical = null;
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return ResolutionStatus.NetworkDrive;
        }

        if (PathSyntax.TryNormalize(finalPath, out string normalized) != PathVerdict.Allowed)
        {
            return ResolutionStatus.Unresolvable;
        }

        canonical = normalized;
        return ResolutionStatus.Resolved;
    }

    private static bool IsNotFound(int error) =>
        error is FileSystemNative.ErrorFileNotFound or FileSystemNative.ErrorPathNotFound;
}
