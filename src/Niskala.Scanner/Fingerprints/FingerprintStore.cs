using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;

namespace Niskala.Scanner.Fingerprints;

public sealed record FingerprintRecord(int SchemaVersion, InstalledApp App, Fingerprint Fingerprint);

public sealed class FingerprintStore(string directory, ILogger<FingerprintStore> logger)
{
    private const int SchemaVersion = 1;
    private const int KeyBytes = 16;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "fingerprints");

    public string? Save(InstalledApp app, Fingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(fingerprint);
        string file = Path.Combine(directory, FileName(app.Id));
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, new FingerprintRecord(SchemaVersion, app, fingerprint), FingerprintJsonContext.Default.FingerprintRecord);
            }

            File.Move(temp, file, overwrite: true);
            ScannerLog.FingerprintSaved(logger, app.Id, file);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScannerLog.FingerprintSaveFailed(logger, app.Id, ex);
            File.Delete(temp);
            return null;
        }
    }

    public IReadOnlyList<FingerprintRecord> LoadAll()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var records = new List<FingerprintRecord>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize(stream, FingerprintJsonContext.Default.FingerprintRecord) is { SchemaVersion: SchemaVersion } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ScannerLog.FingerprintReadFailed(logger, file, ex);
            }
        }

        return records;
    }

    private static string FileName(string appId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(appId.ToUpperInvariant())).AsSpan(0, KeyBytes)) + ".json";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(FingerprintRecord))]
internal sealed partial class FingerprintJsonContext : JsonSerializerContext;
