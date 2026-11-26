using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Guardian.Registry;

namespace Niskala.Guardian.Journal;

public sealed class TransactionStore(string directory, ILogger? logger = null, TimeProvider? time = null)
{
    public const int SchemaVersion = 1;

    private const string RecordFile = "transaction.json";
    private const string BackupFile = "registry.json.gz";
    private const string ExportFile = "registry.reg";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "transactions");

    public string NewId() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_time.GetUtcNow():yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}");

    public void Save(TransactionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Write(record.Id, RecordFile, stream => JsonSerializer.Serialize(stream, record, GuardianJsonContext.Default.TransactionRecord));
    }

    public int SaveRegistryBackup(string id, RegistryBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        Write(id, BackupFile, stream =>
        {
            using var zip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
            JsonSerializer.Serialize(zip, backup, GuardianJsonContext.Default.RegistryBackup);
        });

        int unexported = 0;
        Write(id, ExportFile, stream => unexported = RegFile.Write(stream, backup));
        return unexported;
    }

    public string RegistryExportPath(string id) => Path.Combine(directory, Folder(id), ExportFile);

    public TransactionRecord? Load(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string file = Path.Combine(directory, Folder(id), RecordFile);
        if (!File.Exists(file))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(file);
        return JsonSerializer.Deserialize(stream, GuardianJsonContext.Default.TransactionRecord);
    }

    public RegistryBackup? LoadRegistryBackup(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string file = Path.Combine(directory, Folder(id), BackupFile);
        if (!File.Exists(file))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(file);
        using var zip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize(zip, GuardianJsonContext.Default.RegistryBackup);
    }

    public IReadOnlyList<TransactionRecord> List()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var records = new List<TransactionRecord>();
        foreach (string folder in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if (Load(Path.GetFileName(folder)) is { SchemaVersion: SchemaVersion } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                GuardianLog.TransactionUnreadable(_logger, Path.GetFileName(folder), ex);
            }
        }

        return [.. records.OrderByDescending(r => r.StartedAt)];
    }

    private void Write(string id, string name, Action<Stream> write)
    {
        string folder = Path.Combine(directory, Folder(id));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name);
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Create(temp))
            {
                write(stream);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string Folder(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? id
            : throw new ArgumentException("A transaction id holds only letters, digits and '-'.", nameof(id));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TransactionRecord))]
[JsonSerializable(typeof(RegistryBackup))]
internal sealed partial class GuardianJsonContext : JsonSerializerContext;
