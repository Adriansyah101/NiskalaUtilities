using System.Security.Cryptography;
using System.Text;

namespace Niskala.Updater;

public readonly record struct DownloadProgress(string Name, long Received, long? Total)
{
    public double? Percent => Total is > 0 ? Math.Min(100, Received * 100.0 / Total.Value) : null;
}

public sealed record VerifiedDownload(ReleaseAsset Asset, string Path, string Sha256);

public sealed class UpdateIntegrityException(string message) : Exception(message);

public sealed class ReleaseDownloader(HttpClient http)
{
    public const string FolderPrefix = "niskala-update-";

    private const int ChecksumsLimit = 64 * 1024;
    private const int BufferSize = 128 * 1024;
    private const long ReportEvery = 512 * 1024;

    public static string CreateFolder() => Directory.CreateTempSubdirectory(FolderPrefix).FullName;

    public static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public async Task<VerifiedDownload> DownloadAsync(
        ReleaseAsset asset,
        ReleaseAsset checksums,
        string folder,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(checksums);
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Name.Length == 0)
        {
            throw new UpdateIntegrityException($"{asset.Name} is not a plain file name.");
        }

        IReadOnlyDictionary<string, string> sums = Sha256Sums.Parse(await ReadChecksumsAsync(checksums.DownloadUrl, cancellationToken).ConfigureAwait(false));
        if (!sums.TryGetValue(asset.Name, out string? expected))
        {
            throw new UpdateIntegrityException($"{ReleaseNames.Checksums} has no hash for {asset.Name}.");
        }

        string path = Path.Combine(folder, asset.Name);
        string actual = await SaveAsync(asset, path, progress, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            File.Delete(path);
            throw new UpdateIntegrityException($"{asset.Name} has SHA-256 {actual}, but {ReleaseNames.Checksums} says {expected}; the file was deleted.");
        }

        return new VerifiedDownload(asset, path, actual);
    }

    private async Task<string> ReadChecksumsAsync(Uri url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > ChecksumsLimit)
        {
            throw new UpdateIntegrityException($"{ReleaseNames.Checksums} is larger than {ChecksumsLimit} bytes.");
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[ChecksumsLimit + 1];
        int length = 0;
        int read;
        while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        if (length > ChecksumsLimit)
        {
            throw new UpdateIntegrityException($"{ReleaseNames.Checksums} is larger than {ChecksumsLimit} bytes.");
        }

        return Encoding.UTF8.GetString(buffer, 0, length).TrimStart('﻿');
    }

    private async Task<string> SaveAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            long received = 0;
            long reported = 0;
            progress?.Report(new DownloadProgress(asset.Name, 0, total));
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (received - reported >= ReportEvery)
                {
                    reported = received;
                    progress?.Report(new DownloadProgress(asset.Name, received, total));
                }
            }

            progress?.Report(new DownloadProgress(asset.Name, received, total));
            if (asset.Size > 0 && received != asset.Size)
            {
                throw new UpdateIntegrityException($"{asset.Name} arrived with {received} bytes; the release lists {asset.Size}.");
            }

            await file.DisposeAsync().ConfigureAwait(false);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }
}
