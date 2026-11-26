namespace Niskala.Core.Safety.Registry;

public enum RegistryVerdict
{
    Allowed = 0,
    InvalidSyntax,
    UnknownHive,
    MergedClassesView,
    VirtualHive,
    SystemAccountHive,
    HiveRoot,
    ProtectedTree,
    ProtectedSelf,
    ContainsProtected,
    ProtectedValue,
    ProtectedDefaultValue,
    ServiceBootOrSystemStart,
    ServiceImageInWindows,
    ServiceUnverifiable,
    RevertNotString,
}
