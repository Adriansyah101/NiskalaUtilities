using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niskala.Tracker.Logs;

public sealed class InstallLogStore(string directory, ILogger? logger = null, TimeProvider? time = null)
{
    public const int SchemaVersion = 1;
    public const string Extension = ".nsklog";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "logs");

    public string NewId() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_time.GetUtcNow():yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}");

    public string PathOf(string id) => Path.Combine(directory, CheckId(id) + Extension);

    public void Save(InstallLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(directory);
        string file = PathOf(log.Id);
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Create(temp))
            {
                Write(stream, log);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public InstallLog? Load(string id)
    {
        string file = PathOf(id);
        if (!File.Exists(file))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(file);
        return Read(stream);
    }

    public IReadOnlyList<InstallLog> List()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var logs = new List<InstallLog>();
        foreach (string file in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                logs.Add(Read(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TrackerLog.InstallLogUnreadable(_logger, Path.GetFileName(file), ex);
            }
        }

        return [.. logs.OrderByDescending(l => l.StartedAt)];
    }

    public bool Delete(string id)
    {
        string file = PathOf(id);
        if (!File.Exists(file))
        {
            return false;
        }

        File.Delete(file);
        return true;
    }

    public InstallLog? FindForApp(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        return List().FirstOrDefault(l => l.RolledBackAt is null && string.Equals(l.AppId, appId, StringComparison.OrdinalIgnoreCase));
    }

    public static void Write(Stream stream, InstallLog log)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(log);
        JsonSerializer.Serialize(stream, log, TrackerJsonContext.Default.InstallLog);
    }

    public static InstallLog Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        InstallLog? log;
        try
        {
            log = JsonSerializer.Deserialize(stream, TrackerJsonContext.Default.InstallLog);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a readable install log: {ex.Message}", ex);
        }

        return log switch
        {
            null or { Format: not InstallLog.FormatName } => throw new InvalidDataException("Not a Niskala install log."),
            { SchemaVersion: > SchemaVersion or < 1 } => throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"Install log schema {log.SchemaVersion} is not supported; this Niskala reads schema {SchemaVersion}.")),
            _ => WithEmptyLists(log),
        };
    }

    private static InstallLog WithEmptyLists(InstallLog log) => log with
    {
        Processes = log.Processes ?? [],
        Files = log.Files ?? [],
        Keys = log.Keys ?? [],
        Values = log.Values ?? [],
        FirewallRules = log.FirewallRules ?? [],
        Filtered = log.Filtered ?? new Dictionary<string, int>(),
        Notes = log.Notes ?? [],
    };

    private static string CheckId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? id
            : throw new ArgumentException("A log id holds only letters, digits and '-'.", nameof(id));
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallLog))]
internal sealed partial class TrackerJsonContext : JsonSerializerContext;
