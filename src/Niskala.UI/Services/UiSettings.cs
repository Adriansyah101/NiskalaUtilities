using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Niskala.UI.Services;

internal sealed record UiSettings
{
    public bool LightTheme { get; init; }

    public string Language { get; init; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "id" ? "id" : "en";

    [JsonIgnore]
    public bool Indonesian => Language == "id";

    private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            using FileStream stream = System.IO.File.OpenRead(File);
            return JsonSerializer.Deserialize(stream, UiSettingsContext.Default.UiSettings) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    public void Save(ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            using FileStream stream = System.IO.File.Create(File);
            JsonSerializer.Serialize(stream, this, UiSettingsContext.Default.UiSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiLog.SettingsSaveFailed(logger, ex);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiSettingsContext : JsonSerializerContext;
