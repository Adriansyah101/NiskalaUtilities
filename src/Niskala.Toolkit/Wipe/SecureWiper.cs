using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety;
using Niskala.Interop.FileSystem;

namespace Niskala.Toolkit.Wipe;

public enum WipeMethod
{
    ZeroFill,
    Dod522022M,
    Gutmann,
}

public enum WipeOutcome
{
    Wiped,
    Refused,
    Missing,
    InUse,
    Failed,
    Cancelled,
}

public sealed record WipeResult(string Path, WipeOutcome Outcome, int Passes, long Bytes, string? Detail = null);

public sealed record FreeSpaceResult(string Drive, WipeOutcome Outcome, int Passes, long Bytes, string? Detail = null);

public sealed record WipeProgress(string Path, int Pass, int Passes, long Written, long Total);

public sealed record FreeSpaceOptions
{
    public long? MaxBytes { get; init; }

    public string? Folder { get; init; }
}

public static class WipePatterns
{
    private static readonly byte[][] GutmannFixed =
    [
        [0x55], [0xAA], [0x92, 0x49, 0x24], [0x49, 0x24, 0x92], [0x24, 0x92, 0x49],
        [0x00], [0x11], [0x22], [0x33], [0x44], [0x55], [0x66], [0x77], [0x88], [0x99], [0xAA], [0xBB], [0xCC], [0xDD], [0xEE], [0xFF],
        [0x92, 0x49, 0x24], [0x49, 0x24, 0x92], [0x24, 0x92, 0x49], [0x6D, 0xB6, 0xDB], [0xB6, 0xDB, 0x6D], [0xDB, 0x6D, 0xB6],
    ];

    private const int GutmannRandomPasses = 4;

    public static IReadOnlyList<byte[]?> For(WipeMethod method) => method switch
    {
        WipeMethod.ZeroFill => [[0x00]],
        WipeMethod.Dod522022M => [[0x00], [0xFF], null],
        _ => [.. Enumerable.Repeat<byte[]?>(null, GutmannRandomPasses), .. GutmannFixed, .. Enumerable.Repeat<byte[]?>(null, GutmannRandomPasses)],
    };
}

public sealed class SecureWiper(PathGuard guard)
{
    public const int ChunkBytes = 3 * 349_525;

    private const int RenameRounds = 3;
    private const int NameLength = 12;
    private const int ErrorSharingViolation = 32;
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;
    private const long FillFileLimit = 1L << 30;
    private const string NameAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly DateTime NeutralTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public WipeResult WipeFile(string path, WipeMethod method, IProgress<WipeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PathDecision decision = guard.Check(path);
        if (!decision.IsAllowed)
        {
            return new WipeResult(path, WipeOutcome.Refused, 0, 0, decision.ToString());
        }

        if (!decision.Exists)
        {
            return new WipeResult(path, WipeOutcome.Missing, 0, 0);
        }

        string target = decision.CanonicalPath!;
        FileAttributes attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.Directory) != 0 || decision.IsReparsePoint)
        {
            return new WipeResult(path, WipeOutcome.Refused, 0, 0, "only regular files are wiped; links and folders are not");
        }

        if ((attributes & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0)
        {
            return new WipeResult(path, WipeOutcome.Refused, 0, 0, "compressed, encrypted and sparse files move their data when rewritten");
        }

        IReadOnlyList<byte[]?> passes = WipePatterns.For(method);
        long length;
        try
        {
            File.SetAttributes(target, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            using (var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough))
            {
                if (FileLinkNative.TryGet(stream.SafeFileHandle, out FileLinkInfo link) == 0 && link.Links > 1)
                {
                    File.SetAttributes(target, attributes);
                    return new WipeResult(path, WipeOutcome.Refused, 0, 0, "other names share this file's data (hard links)");
                }

                length = stream.Length;
                for (int pass = 0; pass < passes.Count; pass++)
                {
                    Overwrite(stream, length, passes[pass], target, pass + 1, passes.Count, progress, cancellationToken);
                }

                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
            }

            File.SetCreationTimeUtc(target, NeutralTime);
            File.SetLastWriteTimeUtc(target, NeutralTime);
            File.SetLastAccessTimeUtc(target, NeutralTime);
            File.Delete(Scramble(target));
        }
        catch (OperationCanceledException)
        {
            return new WipeResult(path, WipeOutcome.Cancelled, 0, 0, "cancelled part-way; the file is partly overwritten");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorSharingViolation)
        {
            KeepAttributes(target, attributes);
            return new WipeResult(path, WipeOutcome.InUse, 0, 0, "in use by another program");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KeepAttributes(target, attributes);
            return new WipeResult(path, WipeOutcome.Failed, 0, 0, ex.Message);
        }

        return new WipeResult(path, WipeOutcome.Wiped, passes.Count, length);
    }

    public async Task<FreeSpaceResult> WipeFreeSpaceAsync(
        string drive,
        WipeMethod method,
        FreeSpaceOptions? options = null,
        IProgress<WipeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(drive);
        options ??= new FreeSpaceOptions();
        string root = Path.GetPathRoot(Path.GetFullPath(drive)) ?? drive;
        var info = new DriveInfo(root);
        if (info.DriveType != DriveType.Fixed || !info.IsReady)
        {
            return new FreeSpaceResult(root, WipeOutcome.Refused, 0, 0, "only local fixed drives are wiped");
        }

        string folder = options.Folder ?? Path.Combine(root, "NiskalaWipe-" + RandomName());
        PathDecision decision = guard.Check(folder);
        if (!decision.IsAllowed || decision.Exists)
        {
            return new FreeSpaceResult(root, WipeOutcome.Refused, 0, 0, decision.IsAllowed ? "the wipe folder already exists" : decision.ToString());
        }

        IReadOnlyList<byte[]?> passes = WipePatterns.For(method);
        long budget = Math.Min(info.AvailableFreeSpace, options.MaxBytes ?? long.MaxValue);
        Directory.CreateDirectory(folder);
        var fills = new List<string>();
        long written = 0;
        try
        {
            written = await Task.Run(() => Fill(folder, budget, passes, fills, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FreeSpaceResult(root, WipeOutcome.Cancelled, 0, written, "cancelled; the fill files were removed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FreeSpaceResult(root, WipeOutcome.Failed, 0, written, ex.Message);
        }
        finally
        {
            foreach (string file in fills)
            {
                File.Delete(file);
            }

            Directory.Delete(folder, recursive: true);
        }

        return new FreeSpaceResult(root, WipeOutcome.Wiped, passes.Count, written);
    }

    internal static string RandomName() => RandomNumberGenerator.GetString(NameAlphabet, NameLength);

    private static void KeepAttributes(string path, FileAttributes attributes)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, attributes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
    }

    private static long Fill(string folder, long budget, IReadOnlyList<byte[]?> passes, List<string> fills, IProgress<WipeProgress>? progress, CancellationToken cancellationToken)
    {
        long total = 0;
        while (total < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"fill-{fills.Count:D5}.bin"));
            fills.Add(file);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            long size = Math.Min(FillFileLimit, budget - total);
            long reached = Grow(stream, size, passes[0], file, passes.Count, progress, cancellationToken);
            total += reached;
            if (reached < size)
            {
                break;
            }
        }

        for (int pass = 1; pass < passes.Count; pass++)
        {
            foreach (string file in fills)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                Overwrite(stream, stream.Length, passes[pass], file, pass + 1, passes.Count, progress, cancellationToken);
            }
        }

        return total;
    }

    private static long Grow(FileStream stream, long size, byte[]? pattern, string path, int passes, IProgress<WipeProgress>? progress, CancellationToken cancellationToken)
    {
        byte[] buffer = Buffer(pattern);
        long written = 0;
        while (written < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, size - written);
            if (pattern is null)
            {
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
            }

            try
            {
                stream.Write(buffer, 0, count);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull)
            {
                return written;
            }

            written += count;
            progress?.Report(new WipeProgress(path, 1, passes, written, size));
        }

        stream.Flush(flushToDisk: true);
        return written;
    }

    internal static void Overwrite(FileStream stream, long length, byte[]? pattern, string path, int pass, int passes, IProgress<WipeProgress>? progress, CancellationToken cancellationToken)
    {
        byte[] buffer = Buffer(pattern);
        stream.Position = 0;
        long written = 0;
        while (written < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - written);
            if (pattern is null)
            {
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
            }

            stream.Write(buffer, 0, count);
            written += count;
            progress?.Report(new WipeProgress(path, pass, passes, written, length));
        }

        stream.Flush(flushToDisk: true);
    }

    private static byte[] Buffer(byte[]? pattern)
    {
        byte[] buffer = new byte[ChunkBytes];
        if (pattern is not null)
        {
            for (int i = 0; i < buffer.Length; i += pattern.Length)
            {
                pattern.AsSpan(0, Math.Min(pattern.Length, buffer.Length - i)).CopyTo(buffer.AsSpan(i));
            }
        }

        return buffer;
    }

    private static string Scramble(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string current = path;
        for (int round = 0; round < RenameRounds; round++)
        {
            string next = Path.Combine(folder, RandomName());
            File.Move(current, next);
            current = next;
        }

        return current;
    }
}
