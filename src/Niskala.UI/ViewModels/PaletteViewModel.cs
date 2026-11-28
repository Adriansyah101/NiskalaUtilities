using CommunityToolkit.Mvvm.ComponentModel;
using Niskala.Core.Safety;
using Niskala.UI.Services;
using Niskala.Uninstall.Force;

namespace Niskala.UI.ViewModels;

public sealed record PaletteEntry(string Glyph, string Title, string Subtitle, Action Run)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public bool Matches(string query) =>
        query.Length == 0
        || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Keywords.Any(k => k.Contains(query, StringComparison.CurrentCultureIgnoreCase) || query.Contains(k, StringComparison.CurrentCultureIgnoreCase));
}

public sealed partial class PaletteViewModel : ObservableObject
{
    private const int MaxApps = 6;

    private readonly UiServices _services;

    internal PaletteViewModel(UiServices services) => _services = services;

    public PaletteEntryCollection Results { get; } = [];

    public Strings S => Strings.Current;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    public bool Open()
    {
        if (_services.Navigation.IsLocked || !_services.Apps.Inspector.Leave(FlowExit.Palette))
        {
            IsOpen = false;
            return false;
        }

        Query = string.Empty;
        Rebuild();
        IsOpen = true;
        return true;
    }

    public void Move(int delta)
    {
        if (Results.Count > 0)
        {
            SelectedIndex = (Math.Max(SelectedIndex, 0) + delta + Results.Count) % Results.Count;
        }
    }

    public void Run(PaletteEntry? entry)
    {
        entry ??= SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
        if (entry is null)
        {
            return;
        }

        IsOpen = false;
        entry.Run();
    }

    partial void OnQueryChanged(string value) => Rebuild();

    private void Rebuild()
    {
        string query = Query.Trim();
        Results.Clear();
        foreach (PaletteEntry entry in Commands().Where(c => c.Matches(query)))
        {
            Results.Add(entry);
        }

        if (query.Length > 0)
        {
            foreach (AppItem item in _services.Apps.Items
                .Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(i => !i.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxApps))
            {
                Results.Add(new PaletteEntry("", item.Name, S.PaletteOpenApp, () => _services.Apps.Open(item.App, FlowKind.None)));
                if (!item.App.IsProtected)
                {
                    Results.Add(new PaletteEntry("", S.PaletteForceApp(item.Name), S.PaletteForceAppHint, () => _services.Apps.Open(item.App, FlowKind.Force)));
                }
            }
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }

    public IEnumerable<PaletteEntry> Commands()
    {
        yield return new PaletteEntry("", S.NavPicker, S.PalettePickerHint, () => _services.Picker.StartCommand.Execute(null));
        yield return new PaletteEntry("", S.PaletteForceFolder, S.PaletteForceFolderHint, ForceFolder);
        yield return new PaletteEntry("", S.PaletteRestore, S.PaletteRestoreHint, () =>
        {
            _services.History.SelectLatestRestorable();
            _services.Navigate(Section.History);
        });
        yield return new PaletteEntry("", S.PaletteRecord, S.PaletteRecordHint, _services.Tracker.BrowseAndShow);
        yield return new PaletteEntry("\uE945", S.PaletteQuickScan, S.PaletteQuickScanHint, () => _services.Navigate(Section.QuickScan));
        yield return new PaletteEntry("", S.PaletteStartup, S.PaletteStartupHint, () => _services.Navigate(Section.Resources));
        yield return new PaletteEntry("\uEDA2", S.PaletteDrives, S.PaletteDrivesHint, () => _services.Navigate(Section.Drives)) { Keywords = Strings.DriveKeywords };
        yield return new PaletteEntry("", S.PaletteRepair, S.PaletteRepairHint, () => _services.Navigate(Section.Repair));
        yield return new PaletteEntry("\uE895", S.PaletteUpdates, S.PaletteUpdatesHint, () =>
        {
            _services.Navigate(Section.Updates);
            _services.Updates.CheckCommand.Execute(null);
        });
        yield return new PaletteEntry("", S.PaletteTheme, S.PaletteThemeHint, _services.ToggleTheme);
        yield return new PaletteEntry("", S.PaletteLanguage, S.PaletteLanguageHint, _services.ToggleLanguage);
    }

    private void ForceFolder()
    {
        if (Pickers.Folder() is not { } folder)
        {
            return;
        }

        ForceTarget? target = ForceRemover.ForPath(folder, _services.Host.PathGuard, _services.Apps.Inventory, _services.Host.KnownRoots, out PathDecision decision);
        if (target is null)
        {
            _services.Navigate(Section.Apps);
            _services.Apps.Error = S.FolderRefused(folder, decision.Verdict);
            return;
        }

        _services.Apps.Open(target.App, FlowKind.Force);
    }
}
