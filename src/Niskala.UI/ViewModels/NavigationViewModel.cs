using CommunityToolkit.Mvvm.ComponentModel;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public enum NavGroup
{
    Apps,
    Cleaning,
    Performance,
    Maintenance,
}

public sealed partial class NavPage : ObservableObject
{
    internal NavPage(Section section, string glyph, NavCategory? category)
    {
        Section = section;
        Glyph = glyph;
        Category = category;
    }

    public Section Section { get; }

    public string Glyph { get; }

    public NavCategory? Category { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class NavCategory : ObservableObject
{
    private const string ExpandedChevron = "";
    private const string CollapsedChevron = "";

    private readonly NavigationViewModel _owner;

    internal NavCategory(NavigationViewModel owner, NavGroup group, string glyph)
    {
        _owner = owner;
        Group = group;
        Glyph = glyph;
    }

    public NavGroup Group { get; }

    public string Glyph { get; }

    public NavPageCollection Pages { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(ShowPages), nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial bool IsActive { get; set; }

    public bool IsOpen => IsExpanded && !IsActive && _owner.IsPaneOpen;

    public bool ShowPages => IsExpanded && _owner.IsPaneOpen;

    public bool IsCompact => !_owner.IsPaneOpen;

    public string Chevron => IsExpanded ? ExpandedChevron : CollapsedChevron;

    internal void PaneChanged()
    {
        OnPropertyChanged(nameof(ShowPages));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsCompact));
    }
}

public sealed partial class NavigationViewModel : ObservableObject
{
    public const double OpenPaneWidth = 272;
    public const double CompactPaneWidth = 56;

    public NavigationViewModel()
    {
        Add(NavGroup.Apps, "", (Section.Apps, ""), (Section.Tracker, ""), (Section.Picker, ""));
        Add(NavGroup.Cleaning, "", (Section.QuickScan, ""), (Section.SelectiveClean, ""), (Section.Registry, ""));
        Add(NavGroup.Performance, "", (Section.Resources, ""), (Section.Suspend, ""));
        Add(NavGroup.Maintenance, "", (Section.Storage, ""), (Section.Duplicates, ""), (Section.Wipe, ""), (Section.Repair, ""), (Section.History, ""));
        Updates = new NavPage(Section.Updates, "", null);
        Relabel(Strings.Current);
        Mark(Page(Section.Apps));
    }

    public event Action<Section>? Shown;

    public Func<Section, bool>? CanLeave { get; set; }

    public NavCategoryCollection Categories { get; } = [];

    public NavPage Updates { get; }

    [ObservableProperty]
    public partial Section Selected { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked))]
    public partial bool IsLocked { get; set; }

    public bool IsUnlocked => !IsLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact), nameof(PaneWidth))]
    public partial bool IsPaneOpen { get; set; } = true;

    public bool IsCompact => !IsPaneOpen;

    public double PaneWidth => IsPaneOpen ? OpenPaneWidth : CompactPaneWidth;

    public IEnumerable<NavPage> Pages => Categories.SelectMany(c => c.Pages).Append(Updates);

    public NavPage Page(Section section) => Pages.First(p => p.Section == section);

    public bool Select(Section section)
    {
        NavPage page = Page(section);
        if (page.IsSelected)
        {
            if (page.Category is { } current)
            {
                current.IsExpanded = true;
            }

            return true;
        }

        if (IsLocked || CanLeave?.Invoke(section) == false)
        {
            return false;
        }

        Mark(page);
        Shown?.Invoke(section);
        return true;
    }

    public void Toggle(NavCategory category) => category.IsExpanded = !category.IsExpanded;

    public void TogglePane() => IsPaneOpen = !IsPaneOpen;

    public void Relabel(Strings strings)
    {
        foreach (NavCategory category in Categories)
        {
            category.Title = category.Group switch
            {
                NavGroup.Apps => strings.NavGroupApps,
                NavGroup.Cleaning => strings.NavGroupClean,
                NavGroup.Performance => strings.NavGroupPerformance,
                _ => strings.NavGroupTools,
            };
        }

        foreach (NavPage page in Pages)
        {
            page.Title = strings.NavTitle(page.Section);
        }
    }

    partial void OnIsPaneOpenChanged(bool value)
    {
        foreach (NavCategory category in Categories)
        {
            category.PaneChanged();
        }
    }

    private void Add(NavGroup group, string glyph, params (Section Section, string Glyph)[] pages)
    {
        var category = new NavCategory(this, group, glyph);
        foreach ((Section section, string pageGlyph) in pages)
        {
            category.Pages.Add(new NavPage(section, pageGlyph, category));
        }

        Categories.Add(category);
    }

    private void Mark(NavPage page)
    {
        foreach (NavPage other in Pages)
        {
            other.IsSelected = other == page;
        }

        foreach (NavCategory category in Categories)
        {
            category.IsActive = category == page.Category;
        }

        if (page.Category is { } owner)
        {
            owner.IsExpanded = true;
        }

        Selected = page.Section;
    }
}
