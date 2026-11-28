namespace Niskala.Updater;

public enum UpdateStatus
{
    UpToDate,
    Available,
    NoRelease,
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseAssets(ReleaseAsset? Installer, ReleaseAsset? Checksums)
{
    public static ReleaseAssets None { get; } = new(null, null);

    public bool IsComplete => Installer is not null && Checksums is not null;
}

public sealed record UpdateCheck(
    UpdateStatus Status,
    ReleaseVersion Current,
    ReleaseVersion? Latest,
    string? Title,
    string? Notes,
    Uri? Page,
    DateTimeOffset? Published,
    ReleaseAssets Assets)
{
    public bool IsAvailable => Status == UpdateStatus.Available;
}
