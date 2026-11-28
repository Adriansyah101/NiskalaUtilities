using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Niskala.UI.Services;

public sealed record UiSettings
{
    public bool LightTheme { get; init; }

    public bool SkipUpdateCheck { get; init; }

    public string Language { get; init; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "id" ? IndonesianCode : EnglishCode;

    public string Page { get; init; } = nameof(Section.Apps);

    [JsonIgnore]
    public bool Indonesian => Language == IndonesianCode;

    public const string EnglishCode = "en";
    public const string IndonesianCode = "id";

    private static readonly (Section Section, string Id)[] Pages =
    [
        (Section.Apps, nameof(Section.Apps)),
        (Section.Tracker, nameof(Section.Tracker)),
        (Section.Picker, nameof(Section.Picker)),
        (Section.QuickScan, nameof(Section.QuickScan)),
        (Section.SelectiveClean, nameof(Section.SelectiveClean)),
        (Section.Registry, nameof(Section.Registry)),
        (Section.Resources, nameof(Section.Resources)),
        (Section.Suspend, nameof(Section.Suspend)),
        (Section.Drives, nameof(Section.Drives)),
        (Section.Repair, nameof(Section.Repair)),
        (Section.History, nameof(Section.History)),
        (Section.Updates, nameof(Section.Updates)),
    ];

    public static IReadOnlyList<Section> KnownPages { get; } = [.. Pages.Select(p => p.Section)];

    public static Section StartPage(string? page)
    {
        foreach ((Section section, string id) in Pages)
        {
            if (string.Equals(id, page, StringComparison.Ordinal))
            {
                return section;
            }
        }

        return Section.Apps;
    }

    public static string PageId(Section section)
    {
        foreach ((Section known, string id) in Pages)
        {
            if (known == section)
            {
                return id;
            }
        }

        return nameof(Section.Apps);
    }

    public UiSettings Sanitized() => this with
    {
        Page = PageId(StartPage(Page)),
        Language = Language == IndonesianCode ? IndonesianCode : EnglishCode,
    };

    private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            using FileStream stream = System.IO.File.OpenRead(File);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    public static UiSettings Read(Stream stream) => JsonSerializer.Deserialize(stream, UiSettingsContext.Default.UiSettings) ?? new UiSettings();

    internal void Save(ILogger logger)
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
public sealed partial class UiSettingsContext : JsonSerializerContext;
