namespace Niskala.Core.Safety;

public enum PathVerdict
{
    Allowed = 0,
    Empty,
    TooLong,
    InvalidCharacters,
    NotFullyQualified,
    UncPath,
    DevicePath,
    AlternateDataStream,
    ReservedDeviceName,
    TraversalSegment,
    TrailingDotOrSpace,
    MissingDrive,
    NetworkDrive,
    UnresolvedShortName,
    Unresolvable,
    DriveRoot,
    ProtectedTree,
    ProtectedSelf,
    ContainsProtected,
}
