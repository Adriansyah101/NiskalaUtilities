using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Interop.Icons;

namespace Niskala.Scanner.Icons;

public sealed class IconCache(IconExtractor extractor, IconSourceResolver resolver, string directory, ILogger<IconCache> logger)
{
    public const int Size = 256;

    public const string FilesFolder = "files";

    private const int KeyBytes = 16;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "icons");

    public Task<string?> GetAsync(InstalledApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        return resolver.Resolve(app) is { } source
            ? GetAsync(source, app.Id, directory, shellFallback: true, cancellationToken)
            : Task.FromResult<string?>(null);
    }

    public Task<string?> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.IsPathFullyQualified(path) && File.Exists(path)
            ? GetAsync(new IconSource(IconSourceKind.Resource, path, 0), path, Path.Combine(directory, FilesFolder), shellFallback: false, cancellationToken)
            : Task.FromResult<string?>(null);
    }

    private async Task<string?> GetAsync(IconSource source, string owner, string folder, bool shellFallback, CancellationToken cancellationToken)
    {
        string file = Path.Combine(folder, FileName(source));
        if (File.Exists(file))
        {
            return file;
        }

        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(folder);
            if (source.Kind == IconSourceKind.Image)
            {
                File.Copy(source.Path, temp, overwrite: true);
            }
            else
            {
                IconPixels? pixels = await extractor.ExtractAsync(source, Size, shellFallback, cancellationToken).ConfigureAwait(false);
                if (pixels is null)
                {
                    ScannerLog.IconNotFound(logger, owner, source.Path, source.Index);
                    return null;
                }

                using (FileStream stream = File.Create(temp))
                {
                    PngEncoder.Write(stream, pixels);
                }
            }

            try
            {
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(file))
            {
                ScannerLog.IconKeptExisting(logger, owner, ex);
            }

            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScannerLog.IconWriteFailed(logger, owner, ex);
            return null;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public int Prune(IEnumerable<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledApp app in apps)
        {
            if (resolver.Resolve(app) is { } source)
            {
                keep.Add(FileName(source));
            }
        }

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.png"))
        {
            string name = Path.GetFileName(file);
            if (!IsCacheFileName(name) || keep.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ScannerLog.IconDeleteFailed(logger, name, ex);
            }
        }

        ScannerLog.IconsPruned(logger, deleted, keep.Count);
        return deleted;
    }

    internal static bool IsCacheFileName(string name) =>
        name.Length == (KeyBytes * 2) + 4
        && name.EndsWith(".png", StringComparison.Ordinal)
        && name.AsSpan(0, KeyBytes * 2).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string FileName(IconSource source)
    {
        long written = File.GetLastWriteTimeUtc(source.Path).Ticks;
        string identity = $"{source.Kind}|{source.Path.ToUpperInvariant()}|{source.Index}|{written}|{Size}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, KeyBytes)) + ".png";
    }
}
