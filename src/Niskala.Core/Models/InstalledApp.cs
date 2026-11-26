using Niskala.Core.Commands;
using Niskala.Core.Safety;

namespace Niskala.Core.Models;

[Flags]
public enum AppSources
{
    None = 0,
    Registry = 1,
    Msi = 2,
    Appx = 4,
}

public enum InstallScope
{
    Machine,
    User,
}

public enum RegistryViewKind
{
    None,
    Shared,
    Registry64,
    Registry32,
}

public enum InstallerKind
{
    Unknown,
    Msi,
    InnoSetup,
    Nsis,
    InstallShield,
    WixBurn,
    Squirrel,
    WingetPortable,
    Msix,
    CustomExe,
    Steam,
}

public enum HiddenReason
{
    None,
    NoDisplayName,
    SystemComponent,
    ChildEntry,
    Update,
    Duplicate,
    NoUninstallEntry,
    SparsePackage,
}

public enum InstallLocationOrigin
{
    None,
    Registry,
    InnoAppPath,
    MsiProperty,
    MsiComponents,
    UninstallerDirectory,
    Package,
}

public enum InstallDateOrigin
{
    None,
    Registry,
    KeyLastWrite,
    MsiProperty,
    PackageFolder,
}

public enum PackageSignatureKind
{
    None,
    Developer,
    Enterprise,
    Store,
    System,
}

public sealed record PackageDetails(
    string FullName,
    string FamilyName,
    PackageSignatureKind SignatureKind,
    string? LogoPath,
    string? LogoBackground,
    bool IsDevelopmentMode);

public sealed record InstalledApp
{
    public required string Id { get; init; }

    public required AppSources Sources { get; init; }

    public required InstallScope Scope { get; init; }

    public RegistryViewKind View { get; init; }

    public string? RegistryKey { get; init; }

    public string? KeyName { get; init; }

    public DateTime? KeyLastWriteUtc { get; init; }

    public string? DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    public InstallLocationOrigin InstallLocationOrigin { get; init; }

    public bool InstallLocationIsEstimate =>
        InstallLocationOrigin is InstallLocationOrigin.MsiComponents or InstallLocationOrigin.UninstallerDirectory;

    public PathVerdict? InstallLocationGuard { get; init; }

    public DateOnly? InstallDate { get; init; }

    public InstallDateOrigin InstallDateOrigin { get; init; }

    public long? EstimatedSizeBytes { get; init; }

    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    public string? ModifyPath { get; init; }

    public ParsedCommand? UninstallCommand { get; init; }

    public ParsedCommand? QuietUninstallCommand { get; init; }

    public InstallerKind InstallerKind { get; init; }

    public IReadOnlyList<string> DetectionEvidence { get; init; } = [];

    public Guid? MsiProductCode { get; init; }

    public PackageDetails? Package { get; init; }

    public bool IsProtected { get; init; }

    public bool NoRemove { get; init; }

    public bool NoModify { get; init; }

    public bool NoRepair { get; init; }

    public bool SystemComponent { get; init; }

    public bool IsOrphan { get; init; }

    public bool IsUninstallerMissing { get; init; }

    public string? ParentKeyName { get; init; }

    public string? ReleaseType { get; init; }

    public string? UrlInfoAbout { get; init; }

    public string? HelpLink { get; init; }

    public HiddenReason HiddenReason { get; init; }

    public string? DuplicateOfId { get; init; }

    public bool IsVisible => HiddenReason == HiddenReason.None;
}
