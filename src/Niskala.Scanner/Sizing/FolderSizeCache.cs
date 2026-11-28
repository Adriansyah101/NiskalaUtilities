using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Niskala.Scanner.Sizing;

public readonly record struct FolderStamp(DateTime FolderWrittenUtc, DateTime NewestEntryUtc, int Entries)
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static FolderStamp? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists)
            {
                return null;
            }

            DateTime newest = DateTime.MinValue;
            int entries = 0;
            var listing = new FileSystemEnumerable<DateTime>(path, static (ref FileSystemEntry entry) => entry.LastWriteTimeUtc.UtcDateTime, TopLevel);
            foreach (DateTime written in listing)
            {
                entries++;
                if (written > newest)
                {
                    newest = written;
                }
            }

            return new FolderStamp(folder.LastWriteTimeUtc, newest, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed record FolderSizeCacheEntry(long Bytes, long Files, long Directories, FolderStamp Stamp, DateTime MeasuredUtc);

public sealed class FolderSizeCache
{
    public const int MaxEntries = 4096;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _file;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FolderSizeCacheEntry> _entries;
    private bool _dirty;

    private FolderSizeCache(string file, ILogger logger, TimeProvider clock, Dictionary<string, FolderSizeCacheEntry> entries)
    {
        _file = file;
        _logger = logger;
        _clock = clock;
        _entries = entries;
    }

    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "cache", "folder-sizes.json");

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public static FolderSizeCache Load(string file, ILogger logger, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(logger);
        clock ??= TimeProvider.System;
        var entries = new Dictionary<string, FolderSizeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        bool dropped = false;
        try
        {
            if (File.Exists(file))
            {
                using FileStream stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize(stream, FolderSizeJsonContext.Default.DictionaryStringFolderSizeCacheEntry) is { } stored)
                {
                    DateTime now = clock.GetUtcNow().UtcDateTime;
                    foreach ((string path, FolderSizeCacheEntry entry) in stored)
                    {
                        if (IsFresh(entry, now) && Path.IsPathFullyQualified(path))
                        {
                            entries[path] = entry;
                        }
                        else
                        {
                            dropped = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ScannerLog.CacheReadFailed(logger, file, ex);
            entries.Clear();
        }

        return new FolderSizeCache(file, logger, clock, entries) { _dirty = dropped };
    }

    public FolderSize? TryGet(SizeRequest request, DiskKind kind)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FolderStamp.Read(request.Path) is { } stamp ? TryGet(request, kind, stamp) : Forget(request.Path);
    }

    public FolderSize? TryGet(SizeRequest request, DiskKind kind, FolderStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            if (!_entries.TryGetValue(request.Path, out FolderSizeCacheEntry? entry))
            {
                return null;
            }

            if (entry.Stamp != stamp || !IsFresh(entry, _clock.GetUtcNow().UtcDateTime))
            {
                _entries.Remove(request.Path);
                _dirty = true;
                return null;
            }

            return new FolderSize(request.Id, request.Path, Exists: true, IsLink: false, entry.Bytes, entry.Files, entry.Directories, kind, TimeSpan.Zero);
        }
    }

    public FolderSize Measure(SizeRequest request, DiskKind kind, CancellationToken cancellationToken, Func<SizeRequest, DiskKind, CancellationToken, FolderSize> measure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(measure);
        FolderStamp? stamp = FolderStamp.Read(request.Path);
        if (stamp is { } before && TryGet(request, kind, before) is { } cached)
        {
            return cached;
        }

        FolderSize measured = measure(request, kind, cancellationToken);
        if (stamp is { } taken && FolderStamp.Read(request.Path) == taken)
        {
            Store(measured, taken);
        }

        return measured;
    }

    public void Store(FolderSize size, FolderStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(size);
        if (!size.Exists || size.IsLink || size.Error is not null || !Path.IsPathFullyQualified(size.Path))
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(size.Path))
            {
                string oldest = _entries.MinBy(e => e.Value.MeasuredUtc).Key;
                _entries.Remove(oldest);
            }

            _entries[size.Path] = new FolderSizeCacheEntry(size.Bytes, size.Files, size.Directories, stamp, _clock.GetUtcNow().UtcDateTime);
            _dirty = true;
        }
    }

    public void Save()
    {
        Dictionary<string, FolderSizeCacheEntry> copy;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            copy = new Dictionary<string, FolderSizeCacheEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            _dirty = false;
        }

        string temp = $"{_file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, copy, FolderSizeJsonContext.Default.DictionaryStringFolderSizeCacheEntry);
            }

            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScannerLog.CacheWriteFailed(_logger, _file, ex);
            lock (_lock)
            {
                _dirty = true;
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static bool IsFresh(FolderSizeCacheEntry entry, DateTime now) => now - entry.MeasuredUtc <= MaxAge && entry.MeasuredUtc <= now.AddMinutes(1);

    private FolderSize? Forget(string path)
    {
        lock (_lock)
        {
            _dirty |= _entries.Remove(path);
        }

        return null;
    }
}

[JsonSerializable(typeof(Dictionary<string, FolderSizeCacheEntry>))]
internal sealed partial class FolderSizeJsonContext : JsonSerializerContext;
