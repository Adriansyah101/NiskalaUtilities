using Niskala.UI.Services;
using Niskala.UI.ViewModels;
using Niskala.UI.Views;

namespace Niskala.UI.Tests;

public sealed class ResponsiveLayoutTests
{
    public static TheoryData<int, int, double> Screens => new()
    {
        { 1366, 728, 1.0 },
        { 1366, 728, 1.25 },
        { 1920, 1032, 1.0 },
        { 1920, 1032, 1.25 },
        { 1920, 1032, 1.5 },
        { 1920, 1032, 1.75 },
        { 1920, 1032, 2.0 },
        { 2560, 1392, 2.0 },
        { 3440, 1392, 1.0 },
        { 3840, 2112, 2.5 },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_first_window_fits_the_work_area_of_every_screen(int width, int height, double scale)
    {
        var work = new PixelRect(0, 40, width, height);

        PixelRect start = WindowPlacement.Initial(work, scale);
        (int minWidth, int minHeight) = WindowPlacement.Minimum(work, scale);

        Assert.InRange(start.X, work.X, work.X + work.Width - start.Width);
        Assert.InRange(start.Y, work.Y, work.Y + work.Height - start.Height);
        Assert.True(start.Width <= work.Width && start.Height <= work.Height);
        Assert.True(start.Width >= minWidth && start.Height >= minHeight);
        Assert.True(minWidth <= work.Width && minHeight <= work.Height);
        Assert.True(start.Width <= Math.Round(WindowPlacement.PreferredWidth * scale));
        Assert.Equal(work.X + ((work.Width - start.Width) / 2), start.X);
    }

    [Theory]
    [InlineData(1.0, 960, 640)]
    [InlineData(1.25, 1200, 800)]
    [InlineData(1.5, 1440, 960)]
    [InlineData(2.0, 1920, 1280)]
    public void The_minimum_size_is_960_by_640_DIP_on_a_large_screen(double scale, int width, int height) =>
        Assert.Equal((width, height), WindowPlacement.Minimum(new PixelRect(0, 0, 7680, 4320), scale));

    [Fact]
    public void A_small_screen_at_high_scaling_never_pushes_the_window_off_screen()
    {
        var work = new PixelRect(0, 0, 1366, 728);
        PixelRect start = WindowPlacement.Initial(work, 2.0);
        Assert.Equal((1366, 728), (start.Width, start.Height));
        Assert.Equal((0, 0), (start.X, start.Y));
        Assert.Equal((1366, 728), WindowPlacement.Minimum(work, 2.0));
    }

    [Fact]
    public void A_roomy_screen_gets_the_preferred_size()
    {
        PixelRect start = WindowPlacement.Initial(new PixelRect(0, 0, 2560, 1392), 1.0);
        Assert.Equal((1320, 860), (start.Width, start.Height));
    }

    [Theory]
    [InlineData(1600, 138)]
    [InlineData(1100, 138)]
    [InlineData(960, 138)]
    [InlineData(820, 138)]
    [InlineData(683, 138)]
    public void The_title_bar_shrinks_the_search_box_and_drops_badges_before_they_overlap(double width, double caption)
    {
        TitleBarFit fit = WindowPlacement.FitTitleBar(width, caption);
        double room = width - caption;
        double used = WindowPlacement.BrandWidth + fit.LauncherWidth + (fit.ShowAdmin ? WindowPlacement.AdminWidth : 0) + (fit.ShowVersion ? WindowPlacement.VersionWidth : 0);

        Assert.InRange(fit.LauncherWidth, WindowPlacement.LauncherMinWidth, WindowPlacement.LauncherMaxWidth);
        Assert.True(used <= Math.Max(room, WindowPlacement.BrandWidth + WindowPlacement.LauncherMinWidth), $"{used} > {room}");
        Assert.True(!fit.ShowVersion || fit.ShowAdmin, "the version goes before the administrator badge");
    }

    [Fact]
    public void A_wide_title_bar_keeps_everything()
    {
        Assert.Equal(new TitleBarFit(WindowPlacement.LauncherMaxWidth, true, true), WindowPlacement.FitTitleBar(1600, 138));
        Assert.False(WindowPlacement.FitTitleBar(700, 138).ShowAdmin);
    }

    [Fact]
    public void The_sidebar_collapses_on_a_narrow_window_and_reopens_only_if_it_closed_itself()
    {
        var nav = new NavigationViewModel();
        nav.Fit(1400, 900);
        Assert.True(nav.IsPaneOpen);
        Assert.False(nav.IsDense);

        nav.Fit(NavigationViewModel.CompactBelowWidth - 1, 900);
        Assert.False(nav.IsPaneOpen);
        nav.Fit(NavigationViewModel.ReopenFromWidth - 1, 900);
        Assert.False(nav.IsPaneOpen);
        nav.Fit(NavigationViewModel.ReopenFromWidth, 900);
        Assert.True(nav.IsPaneOpen);

        nav.TogglePane();
        nav.Fit(1600, 900);
        Assert.False(nav.IsPaneOpen);
    }

    [Fact]
    public void A_short_window_packs_the_sidebar_tighter()
    {
        var nav = new NavigationViewModel();
        NavCategory apps = nav.Categories[0];
        var changed = new List<string?>();
        apps.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        nav.Fit(1400, NavigationViewModel.DenseBelowHeight - 1);

        Assert.True(nav.IsDense);
        Assert.Equal(NavigationViewModel.DenseCategoryHeight, apps.ButtonHeight);
        Assert.Equal(NavigationViewModel.DensePageHeight, apps.Pages[0].ButtonHeight);
        Assert.Equal(NavigationViewModel.DenseCategoryHeight, nav.Updates.ButtonHeight);
        Assert.Equal(NavigationViewModel.DensePaneGap, nav.PaneSpacing);
        Assert.Contains(nameof(NavCategory.ButtonHeight), changed);

        nav.Fit(1400, NavigationViewModel.DenseBelowHeight);
        Assert.False(nav.IsDense);
        Assert.Equal(NavigationViewModel.CategoryHeight, apps.ButtonHeight);
        Assert.Equal(NavigationViewModel.PageHeight, apps.Pages[0].ButtonHeight);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void The_cluster_map_draws_whole_pixel_cells_at_every_scale(double scale)
    {
        MapGrid grid = MapGrid.For(600, 180, scale);

        Assert.True(grid.Cells > 0);
        Assert.True(grid.PixelWidth <= Math.Floor(600 * scale));
        Assert.True(grid.PixelHeight <= Math.Floor(180 * scale));
        Assert.Equal((grid.Columns * grid.Pitch) - grid.Gap, grid.PixelWidth);
        Assert.Equal(Math.Round(MapGrid.CellSize * scale), grid.Cell);
        Assert.InRange(grid.Pitch / scale, MapGrid.CellSize + MapGrid.CellGap - 1, MapGrid.CellSize + MapGrid.CellGap + 1);
        Assert.Equal(default, MapGrid.For(0, 180, scale));
    }

    [Theory]
    [InlineData(1400, 600)]
    [InlineData(500, 468)]
    [InlineData(20, 0)]
    public void The_command_palette_stays_inside_a_narrow_window(double available, double width) =>
        Assert.Equal(width, ShellView.PaletteWidthFor(available));

    [Fact]
    public void Pages_use_proportional_columns_instead_of_fixed_pixel_widths()
    {
        foreach (string page in (string[])["HistoryPage", "RepairPage", "TrackerPage", "UpdatesPage", "DriveOptimizerPage", "LeftoverReviewView"])
        {
            string xaml = Repo.Read("src", "Niskala.UI", "Views", page + ".xaml");
            Assert.DoesNotMatch(@"<ColumnDefinition Width=""[0-9]{3}""", xaml);
            Assert.Matches(@"<ColumnDefinition Width=""[0-9]\*""", xaml);
        }

        Assert.DoesNotContain("Width=\"260\"", Repo.Read("src", "Niskala.UI", "Views", "AppsPage.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("StackPanel Width=", Repo.Read("src", "Niskala.UI", "Views", "UpdateOfferView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Both_executables_declare_per_monitor_v2_awareness()
    {
        foreach (string manifest in (string[])[Path.Combine("src", "Niskala.UI", "app.manifest"), Path.Combine("native", "Niskala.Launcher", "src", "Launcher.manifest")])
        {
            string text = Repo.Read(manifest);
            Assert.Contains(">PerMonitorV2</dpiAwareness>", text, StringComparison.Ordinal);
            Assert.Contains(">true/pm</dpiAware>", text, StringComparison.Ordinal);
        }

        string splash = Repo.Read("native", "Niskala.Launcher", "src", "Splash.cpp");
        Assert.Contains("GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI", splash, StringComparison.Ordinal);
        Assert.Contains("FitScale(", splash, StringComparison.Ordinal);
    }
}

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string Find()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Niskala.sln")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("Niskala.sln was not found above the test folder.");
    }
}
