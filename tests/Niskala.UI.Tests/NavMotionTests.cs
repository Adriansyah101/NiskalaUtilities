using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class NavMotionTests
{
    private const double Natural = 200;

    [Fact]
    public void A_full_slide_takes_the_whole_duration()
    {
        Assert.Equal(NavMotion.Accordion, NavMotion.Remaining(0, Natural, Natural, NavMotion.Accordion, enabled: true));
        Assert.Equal(NavMotion.Accordion, NavMotion.Remaining(Natural, 0, Natural, NavMotion.Accordion, enabled: true));
    }

    [Fact]
    public void A_click_halfway_turns_back_from_where_the_list_is_in_half_the_time()
    {
        TimeSpan back = NavMotion.Remaining(Natural / 2, 0, Natural, NavMotion.Accordion, enabled: true);

        Assert.Equal(NavMotion.Accordion / 2, back);
    }

    [Fact]
    public void A_tiny_remaining_distance_still_animates_briefly_and_never_longer_than_a_full_slide()
    {
        Assert.Equal(NavMotion.Shortest, NavMotion.Remaining(199, Natural, Natural, NavMotion.Accordion, enabled: true));
        Assert.Equal(NavMotion.Accordion, NavMotion.Remaining(0, 900, Natural, NavMotion.Accordion, enabled: true));
    }

    [Theory]
    [InlineData(0, 0, Natural)]
    [InlineData(10, 10, Natural)]
    [InlineData(0, Natural, 0)]
    [InlineData(double.NaN, Natural, Natural)]
    public void Nothing_to_move_settles_at_once(double from, double to, double span)
    {
        Assert.Equal(TimeSpan.Zero, NavMotion.Remaining(from, to, span, NavMotion.Accordion, enabled: true));
    }

    [Fact]
    public void Windows_animation_setting_off_makes_every_transition_instant()
    {
        Assert.Equal(TimeSpan.Zero, NavMotion.Remaining(0, Natural, Natural, NavMotion.Accordion, enabled: false));
        Assert.Equal(TimeSpan.Zero, NavMotion.Remaining(NavigationViewModel.OpenPaneWidth, NavigationViewModel.CompactPaneWidth,
            NavigationViewModel.OpenPaneWidth - NavigationViewModel.CompactPaneWidth, NavMotion.Pane, enabled: false));
        Assert.Equal(1f, NavMotion.PressScale(pressed: true, enabled: false));
    }

    [Fact]
    public void The_rail_folds_over_the_whole_pane_duration_and_unfolds_back_from_midway()
    {
        const double span = NavigationViewModel.OpenPaneWidth - NavigationViewModel.CompactPaneWidth;
        double middle = NavigationViewModel.CompactPaneWidth + span / 2;

        Assert.Equal(NavMotion.Pane, NavMotion.Remaining(NavigationViewModel.OpenPaneWidth, NavigationViewModel.CompactPaneWidth, span, NavMotion.Pane, enabled: true));
        Assert.Equal(NavMotion.Pane / 2, NavMotion.Remaining(middle, NavigationViewModel.OpenPaneWidth, span, NavMotion.Pane, enabled: true));
    }

    [Theory]
    [InlineData(true, 180, 180)]
    [InlineData(false, 180, 0)]
    [InlineData(true, -5, 0)]
    [InlineData(true, double.PositiveInfinity, 0)]
    public void The_list_slides_to_its_natural_height_or_to_nothing(bool expanded, double natural, double expected)
    {
        Assert.Equal(expected, NavMotion.AccordionTarget(expanded, natural));
    }

    [Fact]
    public void A_press_shrinks_the_pill_slightly_and_a_release_restores_it()
    {
        float pressed = NavMotion.PressScale(pressed: true, enabled: true);

        Assert.InRange(pressed, 0.9f, 0.99f);
        Assert.Equal(1f, NavMotion.PressScale(pressed: false, enabled: true));
    }

    [Theory]
    [InlineData(true, false, "Active")]
    [InlineData(true, true, "Active")]
    [InlineData(false, true, "Open")]
    [InlineData(false, false, "Plain")]
    public void The_highlight_state_follows_the_category(bool active, bool open, string expected)
    {
        Assert.Equal(expected, NavMotion.EmphasisState(active, open));
    }

    [Fact]
    public void Width_and_guide_states_name_the_template_states()
    {
        Assert.Equal("Compact", NavMotion.WidthState(true));
        Assert.Equal("Full", NavMotion.WidthState(false));
        Assert.Equal("Guide", NavMotion.GuideState(true));
        Assert.Equal("NoGuide", NavMotion.GuideState(false));
    }

    [Fact]
    public void Moving_the_selection_hands_the_highlight_from_one_page_to_the_next()
    {
        var nav = new NavigationViewModel();
        NavPage from = nav.Page(Section.Apps);
        NavPage to = nav.Page(Section.Repair);
        var changes = new List<string>();
        from.PropertyChanged += (_, e) => changes.Add($"from.{e.PropertyName}");
        to.PropertyChanged += (_, e) => changes.Add($"to.{e.PropertyName}");

        nav.Select(Section.Repair);

        Assert.Equal(["from.IsSelected", "to.IsSelected"], changes);
        Assert.Equal("Plain", NavMotion.EmphasisState(from.IsSelected, false));
        Assert.Equal("Active", NavMotion.EmphasisState(to.IsSelected, false));
    }

    [Fact]
    public void Rapid_clicks_on_a_category_never_lock_it_and_end_on_the_last_click()
    {
        var nav = new NavigationViewModel();
        NavCategory cleaning = nav.Categories.Single(c => c.Group == NavGroup.Cleaning);
        var shown = new List<bool>();
        cleaning.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavCategory.ShowPages))
            {
                shown.Add(cleaning.ShowPages);
            }
        };

        for (int click = 0; click < 7; click++)
        {
            nav.Toggle(cleaning);
        }

        Assert.Equal([true, false, true, false, true, false, true], shown);
        Assert.True(cleaning.ShowPages);
    }

    [Fact]
    public void Rapid_clicks_on_the_menu_button_end_on_the_last_width()
    {
        var nav = new NavigationViewModel();
        var widths = new List<double>();
        nav.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationViewModel.PaneWidth))
            {
                widths.Add(nav.PaneWidth);
            }
        };

        for (int click = 0; click < 5; click++)
        {
            nav.TogglePane();
        }

        Assert.Equal(5, widths.Count);
        Assert.Equal(NavigationViewModel.CompactPaneWidth, nav.PaneWidth);
    }
}
