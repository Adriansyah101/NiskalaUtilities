using Niskala.Core.Models;
using Niskala.Interop.Desktop;
using Niskala.Interop.Shell;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Steam;

namespace Niskala.Uninstall.Picker;

public static class TargetResolver
{
    public static PickerHit FromFile(string path, IReadOnlyList<InstalledApp> apps)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return FromItem(path, Label(path), default, PickerTargetKind.File, apps);
    }

    public static PickerHit FromItem(string path, string label, ScreenRect bounds, PickerTargetKind kind, IReadOnlyList<InstalledApp> apps)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(apps);
        string? target = path;
        InstalledApp? app = null;
        PickerMatch match = PickerMatch.None;
        int? steamAppId = null;
        IEnumerable<InstalledApp> ordered = apps.OrderByDescending(a => a.IsVisible);
        switch (Path.GetExtension(path).ToUpperInvariant())
        {
            case ".LNK":
                ShortcutTarget? link = ShortcutNative.Read([path]).FirstOrDefault();
                target = link?.Path;
                if (Guid.TryParse(link?.MsiProductCode, out Guid product) && ordered.FirstOrDefault(a => a.MsiProductCode == product) is { } owner)
                {
                    (app, match) = (owner, PickerMatch.ShortcutProduct);
                }

                break;
            case ".URL":
                InternetShortcut? shortcut = InternetShortcut.Read(path);
                target = shortcut?.LocalPath;
                steamAppId = SteamLibrary.AppIdFromUrl(shortcut?.Url);
                if (steamAppId is { } id && ordered.FirstOrDefault(a => SteamLibrary.AppId(a) == id) is { } game)
                {
                    (app, match) = (game, PickerMatch.SteamAppId);
                }

                break;
        }

        if (app is null)
        {
            (app, match) = WindowPicker.Match(target, null, apps);
        }

        return new PickerHit(0, label, string.Empty, 0, target, null, app, match)
        {
            Kind = kind,
            Bounds = bounds,
            ItemPath = path,
            SteamAppId = steamAppId,
        };
    }

    public static string? ItemFor(string label, IReadOnlyList<DesktopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return label.Length == 0 ? null : items.FirstOrDefault(i => i.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).Path;
    }

    private static string Label(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".LNK" or ".URL" ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
}
