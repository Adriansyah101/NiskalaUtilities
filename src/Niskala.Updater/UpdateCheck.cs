namespace Niskala.Updater;

public enum UpdateStatus
{
    UpToDate,
    Available,
    NoRelease,
}

public enum UpdateProduct
{
    Desktop,
    CommandLine,
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseAssets(ReleaseAsset? Installer, ReleaseAsset? Portable, ReleaseAsset? Cli, ReleaseAsset? Checksums)
{
    public static ReleaseAssets None { get; } = new(null, null, null, null);

    public ReleaseAsset? For(InstallMode mode, UpdateProduct product) => mode switch
    {
        InstallMode.Installed => Installer,
        _ => product == UpdateProduct.Desktop ? Portable : Cli,
    };
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
