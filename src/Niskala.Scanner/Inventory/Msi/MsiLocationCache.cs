using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Niskala.Scanner.Inventory.Msi;

public sealed class MsiLocationCache
{
    private readonly string _file;
    private readonly ILogger _logger;
    private readonly Dictionary<string, MsiLocationCacheEntry> _entries;
    private bool _dirty;

    private MsiLocationCache(string file, ILogger logger, Dictionary<string, MsiLocationCacheEntry> entries)
    {
        _file = file;
        _logger = logger;
        _entries = entries;
    }

    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "cache", "msi-locations.json");

    public static MsiLocationCache Load(string file, ILogger<MsiLocationCache> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        Dictionary<string, MsiLocationCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(file))
            {
                using FileStream stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize(stream, MsiCacheJsonContext.Default.DictionaryStringMsiLocationCacheEntry) is { } stored)
                {
                    entries = new Dictionary<string, MsiLocationCacheEntry>(stored, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ScannerLog.CacheReadFailed(logger, file, ex);
        }

        return new MsiLocationCache(file, logger, entries);
    }

    public bool TryGet(Guid productCode, DateTime stampUtc, out string? location)
    {
        if (_entries.TryGetValue(Key(productCode), out MsiLocationCacheEntry? entry) && entry.StampUtc == stampUtc)
        {
            location = entry.Location;
            return true;
        }

        location = null;
        return false;
    }

    public void Set(Guid productCode, DateTime stampUtc, string? location)
    {
        _entries[Key(productCode)] = new MsiLocationCacheEntry(stampUtc, location);
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        string temp = _file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, _entries, MsiCacheJsonContext.Default.DictionaryStringMsiLocationCacheEntry);
            }

            File.Move(temp, _file, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScannerLog.CacheWriteFailed(_logger, _file, ex);
        }
    }

    private static string Key(Guid productCode) => productCode.ToString("B").ToUpperInvariant();
}

public sealed record MsiLocationCacheEntry(DateTime StampUtc, string? Location);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, MsiLocationCacheEntry>))]
internal sealed partial class MsiCacheJsonContext : JsonSerializerContext;
