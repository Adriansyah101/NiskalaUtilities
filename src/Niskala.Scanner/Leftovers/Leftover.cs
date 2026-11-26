namespace Niskala.Scanner.Leftovers;

public enum LeftoverKind
{
    Folder,
    File,
    RegistryKey,
    RegistryValue,
    FirewallRule,
    ScheduledTask,
}

public enum ConfidenceTier
{
    Low,
    Review,
    Safe,
}

public enum LeftoverSignal
{
    InstallerIdentity,
    InFingerprint,
    PointsToFingerprint,
    ExactName,
    PublisherAndProduct,
    NearInstallDate,
    PartialName,
    OtherInstalledApp,
    RecordedAtInstall,
    SteamAppId,
    PersonalData,
    MissingProgram,
    PackageFamily,
}

public enum ExclusionReason
{
    Protected,
    MicrosoftSigned,
    TrustedInstallerOwned,
    OtherAppFolder,
}

public sealed record LeftoverReason(LeftoverSignal Signal, int Weight, string Detail);

public sealed record LeftoverItem(
    LeftoverKind Kind,
    string Path,
    string? ValueName,
    int Score,
    ConfidenceTier Tier,
    IReadOnlyList<LeftoverReason> Reasons)
{
    public int? Files { get; init; }

    public long? Bytes { get; init; }
}

public sealed record ExcludedLeftover(LeftoverKind Kind, string Path, string? ValueName, ExclusionReason Reason, string Detail);

public sealed record LeftoverReport
{
    public required string AppId { get; init; }

    public string? AppName { get; init; }

    public required DateTimeOffset ScannedAt { get; init; }

    public TimeSpan Elapsed { get; init; }

    public required IReadOnlyList<LeftoverItem> Items { get; init; }

    public IReadOnlyList<ExcludedLeftover> Excluded { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}
