using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void The_rail_holds_four_categories_with_their_pages_in_order()
    {
        var nav = new NavigationViewModel();

        Assert.Equal([NavGroup.Apps, NavGroup.Cleaning, NavGroup.Performance, NavGroup.Maintenance], nav.Categories.Select(c => c.Group));
        Assert.Equal([Section.Apps, Section.Tracker, Section.Picker], Pages(nav, NavGroup.Apps));
        Assert.Equal([Section.QuickScan, Section.SelectiveClean, Section.Registry], Pages(nav, NavGroup.Cleaning));
        Assert.Equal([Section.Resources, Section.Suspend], Pages(nav, NavGroup.Performance));
        Assert.Equal([Section.Storage, Section.Duplicates, Section.Wipe, Section.Repair, Section.History], Pages(nav, NavGroup.Maintenance));
    }

    [Fact]
    public void Every_section_has_one_place_and_only_Updates_sits_outside_a_category()
    {
        var nav = new NavigationViewModel();

        Assert.Equal(Enum.GetValues<Section>().Order(), nav.Pages.Select(p => p.Section).Order());
        Assert.Equal([Section.Updates], nav.Pages.Where(p => p.Category is null).Select(p => p.Section));
        Assert.Null(nav.Updates.Category);
    }

    [Fact]
    public void The_app_starts_on_Apps_with_only_its_category_open()
    {
        var nav = new NavigationViewModel();

        Assert.Equal(Section.Apps, nav.Selected);
        Assert.True(nav.Page(Section.Apps).IsSelected);
        Assert.Equal([NavGroup.Apps], nav.Categories.Where(c => c.IsExpanded).Select(c => c.Group));
        Assert.Equal([NavGroup.Apps], nav.Categories.Where(c => c.IsActive).Select(c => c.Group));
        Assert.True(nav.IsPaneOpen);
        Assert.Equal(NavigationViewModel.OpenPaneWidth, nav.PaneWidth);
    }

    [Fact]
    public void Clicking_a_category_opens_and_closes_it_without_changing_the_page()
    {
        var nav = new NavigationViewModel();
        NavCategory cleaning = Category(nav, NavGroup.Cleaning);
        var shown = new List<Section>();
        nav.Shown += shown.Add;

        nav.Toggle(cleaning);
        Assert.True(cleaning.IsExpanded);
        Assert.True(cleaning.ShowPages);
        Assert.False(cleaning.IsActive);

        nav.Toggle(cleaning);
        Assert.False(cleaning.IsExpanded);
        Assert.False(cleaning.ShowPages);

        Assert.Equal(Section.Apps, nav.Selected);
        Assert.Empty(shown);
    }

    [Fact]
    public void An_open_category_that_holds_no_selected_page_gets_the_neutral_pill()
    {
        var nav = new NavigationViewModel();
        NavCategory apps = Category(nav, NavGroup.Apps);
        NavCategory performance = Category(nav, NavGroup.Performance);

        nav.Toggle(performance);

        Assert.True(performance.IsOpen);
        Assert.False(performance.IsActive);
        Assert.True(apps.IsActive);
        Assert.False(apps.IsOpen);
        Assert.False(Category(nav, NavGroup.Cleaning).IsOpen);
    }

    [Fact]
    public void Collapsing_the_active_category_keeps_its_highlight()
    {
        var nav = new NavigationViewModel();
        NavCategory apps = Category(nav, NavGroup.Apps);

        nav.Toggle(apps);

        Assert.False(apps.IsExpanded);
        Assert.True(apps.IsActive);
        Assert.True(nav.Page(Section.Apps).IsSelected);
    }

    [Fact]
    public void Choosing_a_page_highlights_it_and_its_category_and_shows_it_once()
    {
        var nav = new NavigationViewModel();
        var shown = new List<Section>();
        nav.Shown += shown.Add;
        nav.Toggle(Category(nav, NavGroup.Cleaning));

        nav.Select(Section.Duplicates);

        Assert.Equal([Section.Duplicates], shown);
        Assert.Equal(Section.Duplicates, nav.Selected);
        Assert.Equal([Section.Duplicates], nav.Pages.Where(p => p.IsSelected).Select(p => p.Section));
        Assert.Equal([NavGroup.Maintenance], nav.Categories.Where(c => c.IsActive).Select(c => c.Group));
        Assert.Equal([NavGroup.Apps, NavGroup.Cleaning, NavGroup.Maintenance], nav.Categories.Where(c => c.IsExpanded).Select(c => c.Group));
        Assert.True(Category(nav, NavGroup.Apps).IsOpen);
    }

    [Theory]
    [InlineData(Section.Repair, NavGroup.Maintenance)]
    [InlineData(Section.Resources, NavGroup.Performance)]
    [InlineData(Section.QuickScan, NavGroup.Cleaning)]
    [InlineData(Section.Storage, NavGroup.Maintenance)]
    [InlineData(Section.History, NavGroup.Maintenance)]
    [InlineData(Section.Picker, NavGroup.Apps)]
    [InlineData(Section.Tracker, NavGroup.Apps)]
    public void A_page_opened_from_the_command_palette_opens_its_collapsed_category(Section section, NavGroup group)
    {
        var nav = new NavigationViewModel();
        nav.Select(Section.Wipe);
        foreach (NavCategory open in nav.Categories.Where(c => c.IsExpanded).ToArray())
        {
            nav.Toggle(open);
        }

        nav.Select(section);

        NavCategory category = Category(nav, group);
        Assert.True(category.IsExpanded);
        Assert.True(category.IsActive);
        Assert.True(category.ShowPages);
        Assert.True(nav.Page(section).IsSelected);
        Assert.Equal([group], nav.Categories.Where(c => c.IsExpanded).Select(c => c.Group));
    }

    [Fact]
    public void Opening_the_page_already_shown_reopens_its_category_without_showing_it_again()
    {
        var nav = new NavigationViewModel();
        nav.Select(Section.Repair);
        NavCategory maintenance = Category(nav, NavGroup.Maintenance);
        nav.Toggle(maintenance);
        var shown = new List<Section>();
        nav.Shown += shown.Add;

        nav.Select(Section.Repair);

        Assert.True(maintenance.IsExpanded);
        Assert.Empty(shown);
    }

    [Fact]
    public void Updates_clears_every_category_highlight_but_leaves_them_open()
    {
        var nav = new NavigationViewModel();
        nav.Select(Section.Suspend);

        nav.Select(Section.Updates);

        Assert.True(nav.Updates.IsSelected);
        Assert.Equal([Section.Updates], nav.Pages.Where(p => p.IsSelected).Select(p => p.Section));
        Assert.DoesNotContain(nav.Categories, c => c.IsActive);
        Assert.Equal([NavGroup.Apps, NavGroup.Performance], nav.Categories.Where(c => c.IsExpanded).Select(c => c.Group));
    }

    [Fact]
    public void The_collapsed_rail_hides_every_page_list_and_restores_them_when_reopened()
    {
        var nav = new NavigationViewModel();
        nav.Toggle(Category(nav, NavGroup.Cleaning));
        var changed = new List<string?>();
        Category(nav, NavGroup.Cleaning).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        nav.TogglePane();

        Assert.False(nav.IsPaneOpen);
        Assert.True(nav.IsCompact);
        Assert.Equal(NavigationViewModel.CompactPaneWidth, nav.PaneWidth);
        Assert.All(nav.Categories, c => Assert.False(c.ShowPages));
        Assert.All(nav.Categories, c => Assert.False(c.IsOpen));
        Assert.All(nav.Categories, c => Assert.True(c.IsCompact));
        Assert.Contains(nameof(NavCategory.ShowPages), changed);
        Assert.Contains(nameof(NavCategory.IsOpen), changed);

        nav.Select(Section.Wipe);
        Assert.True(Category(nav, NavGroup.Maintenance).IsActive);
        Assert.False(Category(nav, NavGroup.Maintenance).ShowPages);

        nav.TogglePane();

        Assert.Equal([NavGroup.Apps, NavGroup.Cleaning, NavGroup.Maintenance], nav.Categories.Where(c => c.ShowPages).Select(c => c.Group));
        Assert.Equal([NavGroup.Apps, NavGroup.Cleaning], nav.Categories.Where(c => c.IsOpen).Select(c => c.Group));
    }

    [Fact]
    public void The_chevron_points_up_only_while_the_category_is_open()
    {
        var nav = new NavigationViewModel();
        NavCategory apps = Category(nav, NavGroup.Apps);
        string open = apps.Chevron;

        nav.Toggle(apps);

        Assert.NotEqual(open, apps.Chevron);
        Assert.Equal(Category(nav, NavGroup.Cleaning).Chevron, apps.Chevron);
    }

    [Theory]
    [InlineData(false, "Apps", "Cleaning", "Performance", "Maintenance", "Quick Scan", "History & backups")]
    [InlineData(true, "Aplikasi", "Pembersihan", "Kinerja", "Pemeliharaan", "Pindai Cepat", "Riwayat & Cadangan")]
    public void Labels_follow_the_language(bool indonesian, string apps, string cleaning, string performance, string maintenance, string quickScan, string history)
    {
        var nav = new NavigationViewModel();

        nav.Relabel(Strings.For(indonesian));

        Assert.Equal([apps, cleaning, performance, maintenance], nav.Categories.Select(c => c.Title));
        Assert.Equal(quickScan, nav.Page(Section.QuickScan).Title);
        Assert.Equal(history, nav.Page(Section.History).Title);
        Assert.All(nav.Pages, p => Assert.False(string.IsNullOrWhiteSpace(p.Title)));
    }

    private static NavCategory Category(NavigationViewModel nav, NavGroup group) => nav.Categories.Single(c => c.Group == group);

    private static Section[] Pages(NavigationViewModel nav, NavGroup group) => [.. Category(nav, group).Pages.Select(p => p.Section)];
}
