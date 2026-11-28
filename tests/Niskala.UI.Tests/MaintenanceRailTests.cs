using System.Text.Json;
using Niskala.Optimizer.Drives;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class MaintenanceRailTests
{
    [Fact]
    public void Maintenance_holds_only_drive_optimizer_repair_and_history()
    {
        var nav = new NavigationViewModel();
        NavCategory maintenance = nav.Categories.Single(c => c.Group == NavGroup.Maintenance);

        Assert.Equal([Section.Drives, Section.Repair, Section.History], maintenance.Pages.Select(p => p.Section));
        Assert.Equal("Drive Optimizer", Strings.For(false).NavTitle(Section.Drives));
        Assert.Equal("Optimasi Drive", Strings.For(true).NavTitle(Section.Drives));
    }

    [Fact]
    public void The_removed_tools_left_no_section_page_or_string()
    {
        string[] gone = ["Storage", "Duplicates", "Wipe"];
        Assert.DoesNotContain(Enum.GetNames<Section>(), n => gone.Contains(n));
        Assert.DoesNotContain(typeof(Strings).GetProperties(), p => p.Name is "NavStorage" or "NavDuplicates" or "NavWipe" or "PaletteStorage" or "WipeSubtitle" or "StorageSubtitle" or "DuplicatesSubtitle");
        foreach (string file in (string[])["StorageMapPage", "DuplicateScanPage", "SecureWipePage"])
        {
            Assert.False(File.Exists(Path.Combine(Repo.Root, "src", "Niskala.UI", "Views", file + ".xaml")), file);
        }

        Assert.False(File.Exists(Path.Combine(Repo.Root, "src", "Niskala.UI", "ViewModels", "ToolkitViewModels.cs")));
        Assert.False(Directory.Exists(Path.Combine(Repo.Root, "src", "Niskala.Toolkit")));
        string palette = Repo.Read("src", "Niskala.UI", "ViewModels", "PaletteViewModel.cs") + Repo.Read("src", "Niskala.UI", "Services", "Strings.Maintenance.cs");
        foreach (string word in (string[])["wipe", "duplicate", "storage map", "Section.Storage"])
        {
            Assert.DoesNotContain(word, palette, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Drive Optimizer")]
    [InlineData("optimasi drive")]
    [InlineData("defrag")]
    [InlineData("Defragment")]
    [InlineData("TRIM")]
    [InlineData("ssd")]
    [InlineData("HDD")]
    [InlineData("optimize")]
    public void Ctrl_K_finds_the_drive_optimizer_by_its_keywords(string query)
    {
        var entry = new PaletteEntry("\uEDA2", "Drive Optimizer", string.Empty, () => { }) { Keywords = Strings.DriveKeywords };
        Assert.True(entry.Matches(query));
        Assert.False(entry.Matches("firewall"));
        Assert.False(new PaletteEntry("\uE90F", "System Repair", string.Empty, () => { }).Matches("defrag"));
    }

    [Fact]
    public void The_palette_lists_the_drive_optimizer_with_its_keywords()
    {
        string palette = Repo.Read("src", "Niskala.UI", "ViewModels", "PaletteViewModel.cs");
        Assert.Contains("Section.Drives", palette, StringComparison.Ordinal);
        Assert.Contains("Keywords = Strings.DriveKeywords", palette, StringComparison.Ordinal);
        Assert.Contains("c.Matches(query)", palette, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Storage")]
    [InlineData("Duplicates")]
    [InlineData("Wipe")]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("drives")]
    [InlineData(null)]
    public void A_page_from_an_older_version_opens_apps(string? page)
    {
        Assert.Equal(Section.Apps, UiSettings.StartPage(page));
        Assert.Equal(nameof(Section.Apps), (new UiSettings { Page = page! }).Sanitized().Page);
    }

    [Fact]
    public void A_saved_page_that_still_exists_is_kept()
    {
        Assert.Equal(Section.Drives, UiSettings.StartPage("Drives"));
        Assert.Equal(Section.Repair, UiSettings.StartPage(UiSettings.PageId(Section.Repair)));
        Assert.Equal(Enum.GetValues<Section>().Order(), UiSettings.KnownPages.Order());
        foreach (Section section in Enum.GetValues<Section>())
        {
            Assert.Equal(section, UiSettings.StartPage(UiSettings.PageId(section)));
        }
    }

    [Fact]
    public void Old_settings_files_are_read_and_cleaned()
    {
        using var stream = new MemoryStream("""{ "lightTheme": true, "language": "id", "page": "Wipe", "storageRoot": "D:\\" }"""u8.ToArray());
        UiSettings old = UiSettings.Read(stream);
        UiSettings clean = old.Sanitized();

        Assert.True(clean.LightTheme);
        Assert.True(clean.Indonesian);
        Assert.Equal(nameof(Section.Apps), clean.Page);
        Assert.NotEqual(old, clean);
        Assert.Equal(clean, clean.Sanitized());
        Assert.Equal("en", (new UiSettings { Language = "fr" }).Sanitized().Language);
        Assert.Contains("\"page\":\"Apps\"", JsonSerializer.Serialize(clean, UiSettingsContext.Default.UiSettings), StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_restores_and_remembers_the_page_through_the_sanitizer()
    {
        string app = Repo.Read("src", "Niskala.UI", "App.xaml.cs");
        Assert.Contains("loaded.Sanitized()", app, StringComparison.Ordinal);
        Assert.Contains("Navigation.Select(UiSettings.StartPage(Settings.Page))", app, StringComparison.Ordinal);
        Assert.Contains("Page = UiSettings.PageId(section)", app, StringComparison.Ordinal);
    }
}

public sealed class SidebarToggleTests
{
    [Theory]
    [InlineData(false, false, "Dark theme", "English")]
    [InlineData(false, true, "Light theme", "English")]
    [InlineData(true, false, "Tema gelap", "Bahasa Indonesia")]
    [InlineData(true, true, "Tema terang", "Bahasa Indonesia")]
    public void The_toggles_name_the_theme_and_language_in_use(bool indonesian, bool light, string theme, string language)
    {
        Strings strings = Strings.For(indonesian);
        Assert.Equal(theme, strings.ThemeName(light));
        Assert.Equal(language, strings.LanguageName);
        Assert.Contains(strings.ThemeName(!light).ToLower(strings.Culture), strings.SwitchThemeHint(light).ToLower(strings.Culture), StringComparison.Ordinal);
        Assert.Contains(indonesian ? "English" : "Bahasa Indonesia", strings.SwitchLanguageHint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_theme_toggle_follows_the_theme_in_use()
    {
        var nav = new NavigationViewModel();
        var changed = new List<string?>();
        nav.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        nav.IsLightTheme = false;
        Assert.Equal(Strings.Current.ThemeName(false), nav.ThemeLabel);
        Assert.Equal(NavigationViewModel.DarkGlyph, nav.ThemeGlyph);

        nav.IsLightTheme = true;
        Assert.Equal(Strings.Current.ThemeName(true), nav.ThemeLabel);
        Assert.Equal(Strings.Current.SwitchThemeHint(true), nav.ThemeHint);
        Assert.Equal(NavigationViewModel.LightGlyph, nav.ThemeGlyph);
        Assert.Contains(nameof(NavigationViewModel.ThemeLabel), changed);
        Assert.Contains(nameof(NavigationViewModel.ThemeGlyph), changed);
        Assert.Equal(Strings.Current.LanguageName, nav.LanguageLabel);
    }

    [Fact]
    public void Both_toggles_bind_their_state_with_a_switch_icon_and_tooltip_in_both_widths()
    {
        string shell = Repo.Read("src", "Niskala.UI", "Views", "ShellView.xaml");
        foreach (string toggle in (string[])["Theme", "Language"])
        {
            Assert.Contains($"Label=\"{{x:Bind Nav.{toggle}Label, Mode=OneWay}}\"", shell, StringComparison.Ordinal);
            Assert.Contains($"ToolTipService.ToolTip=\"{{x:Bind Nav.{toggle}Hint, Mode=OneWay}}\"", shell, StringComparison.Ordinal);
        }

        Assert.Contains("Glyph=\"{x:Bind Nav.ThemeGlyph, Mode=OneWay}\"", shell, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(shell, "TrailingGlyph=\"&#xE8AB;\""));
        Assert.DoesNotContain("SwitchLanguage}", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("LabelThemeButton", Repo.Read("src", "Niskala.UI", "Views", "ShellView.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("_services.Navigation.IsLightTheme = light;", Repo.Read("src", "Niskala.UI", "MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_theme_brush_exists_in_both_themes_and_pages_follow_theme_changes()
    {
        string app = Repo.Read("src", "Niskala.UI", "App.xaml");
        int light = app.IndexOf("<ResourceDictionary x:Key=\"Light\">", StringComparison.Ordinal);
        string[] dark = Keys(app[..light]);
        string[] bright = Keys(app[light..app.IndexOf("</ResourceDictionary.ThemeDictionaries>", StringComparison.Ordinal)]);
        Assert.Equal(dark.Order(), bright.Order());
        foreach (string key in (string[])["NiskalaMapFreeBrush", "NiskalaMapUsedBrush", "NiskalaMapFragmentedBrush", "NiskalaMapArchiveBrush", "NiskalaMapSystemBrush", "NiskalaScrimBrush", "NiskalaNavGuideBrush", "NiskalaPaneBrush"])
        {
            Assert.Contains(key, dark);
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo.Root, "src", "Niskala.UI"), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string xaml = File.ReadAllText(file);
            foreach (string key in dark)
            {
                Assert.DoesNotContain($"{{StaticResource {key}}}", xaml, StringComparison.Ordinal);
            }
        }

        string window = Repo.Read("src", "Niskala.UI", "MainWindow.xaml.cs");
        foreach (string caption in (string[])["ButtonForegroundColor", "ButtonHoverBackgroundColor", "ButtonPressedBackgroundColor", "ButtonInactiveForegroundColor"])
        {
            Assert.Contains($"bar.{caption} = light ?", window, StringComparison.Ordinal);
        }

        Assert.Contains("ForgetHiddenPages();", Repo.Read("src", "Niskala.UI", "Views", "ShellView.xaml.cs"), StringComparison.Ordinal);
    }

    private static string[] Keys(string dictionary) =>
        [.. System.Text.RegularExpressions.Regex.Matches(dictionary, "x:Key=\"(?<key>Niskala[A-Za-z]+Brush)\"").Select(m => m.Groups["key"].Value)];
}

public sealed class DriveOptimizerPageTests
{
    private static DriveVolume Volume(MediaKind media) => new('D', "Data", "NTFS", 4096, 1_000_000, 4_096_000_000, 2_048_000_000, media, media == MediaKind.Ssd);

    [Theory]
    [InlineData(false, MediaKind.Hdd, "Defragment")]
    [InlineData(false, MediaKind.Ssd, "Optimize (TRIM)")]
    [InlineData(true, MediaKind.Hdd, "Defragmentasi")]
    [InlineData(true, MediaKind.Ssd, "Optimalkan (TRIM)")]
    public void The_main_button_follows_the_drive_kind(bool indonesian, MediaKind media, string label) =>
        Assert.Equal(label, DriveOptimizerViewModel.PrimaryLabel(Strings.For(indonesian), Volume(media)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_mode_has_a_neutral_name_and_a_hint(bool indonesian)
    {
        Strings strings = Strings.For(indonesian);
        string[] names = [.. Enum.GetValues<DefragMode>().Select(strings.ModeName)];
        Assert.Equal(3, names.Distinct().Count());
        Assert.All(Enum.GetValues<DefragMode>(), m => Assert.False(string.IsNullOrWhiteSpace(strings.ModeHint(m))));
        Assert.Equal(indonesian ? ["Defrag cepat", "Defrag & rapikan ruang kosong", "Optimasi zona penuh"] : ["Quick Defrag", "Consolidate Free Space", "Full Zone Optimization"], names);
    }

    [Fact]
    public void The_health_tab_lists_every_figure_with_text()
    {
        var summary = new DriveSummary(1_000_000, 400_000, 1_200, 34, 120, 12.5, 500_000, 100, 1_000, 100, 4, 2, 1);
        IReadOnlyList<SummaryRow> rows = DriveOptimizerViewModel.SummaryRows(Strings.For(false), Volume(MediaKind.Hdd), summary, DateTimeOffset.Now);

        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Label) || string.IsNullOrWhiteSpace(r.Value)));
        Assert.Contains(rows, r => r.Label == "Excess fragments" && r.Value == "120");
        Assert.Contains(rows, r => r.Label == "Largest free block" && r.Value.Contains("GB", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Value.Contains("Fast 100", StringComparison.Ordinal) && r.Value.Contains("Archive 100", StringComparison.Ordinal));
    }

    [Fact]
    public void The_fragmented_tab_works_only_for_hard_disks_and_has_checkboxes_and_zones()
    {
        string page = Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml");
        Assert.Contains("x:Name=\"FragmentsTab\" Text=\"{x:Bind ViewModel.S.TabFragmented}\" IsEnabled=\"{x:Bind ViewModel.IsHdd, Mode=OneWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind IsChecked, Mode=TwoWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ZoneText}", page, StringComparison.Ordinal);
        Assert.Contains("{x:Bind FragmentsText}", page, StringComparison.Ordinal);
        Assert.Contains("Content=\"{x:Bind ViewModel.S.DefragChecked}\"", page, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"False\" Minimum=\"0\" Maximum=\"100\"", page, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind services:Format.VisibleText(ViewModel.Banner.Message), Mode=OneWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("FragmentedBrush=\"{ThemeResource NiskalaMapFragmentedBrush}\"", page, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind services:Format.Visible(ViewModel.ShowModes), Mode=OneWay}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Defrag_checked_sends_only_the_checked_paths_in_quick_mode()
    {
        string model = Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");
        Assert.Contains("Fragments.Where(f => f.IsChecked).Select(f => f.Path)", model, StringComparison.Ordinal);
        Assert.Contains("DefragmentAsync(row, DefragMode.Quick, chosen)", model, StringComparison.Ordinal);
        Assert.Contains("row.IsSsd ? TrimAsync(row) : DefragmentAsync(row, Mode, null)", model, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_run_keeps_its_last_progress_instead_of_showing_it_complete()
    {
        string model = Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");
        Assert.Contains("if (!cancel.IsCancellationRequested)\r\n            {\r\n                Percent = 100;", model, StringComparison.Ordinal);
        Assert.DoesNotContain("await work(cancel.Token);\r\n            Percent = 100;", model, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, MediaKind.Hdd, StorageBus.Usb, 4800, false, "HDD · NTFS · USB · 4,800 rpm")]
    [InlineData(true, MediaKind.Hdd, StorageBus.Usb, 4800, false, "HDD · NTFS · USB · 4.800 rpm")]
    [InlineData(false, MediaKind.Hdd, StorageBus.Sata, 0, false, "HDD · NTFS · SATA")]
    [InlineData(false, MediaKind.Ssd, StorageBus.Nvme, 0, true, "SSD · NTFS · NVMe · TRIM on")]
    [InlineData(true, MediaKind.Ssd, StorageBus.Usb, 1, false, "SSD · NTFS · USB · tanpa TRIM")]
    [InlineData(false, MediaKind.Unknown, StorageBus.Unknown, 0, false, "Unknown · NTFS")]
    public void The_drive_facts_name_the_bus_and_the_spindle_speed(bool indonesian, MediaKind media, StorageBus bus, int rotation, bool trim, string expected)
    {
        var volume = new DriveVolume('E', "Data", "NTFS", 4096, 1_000_000, 4_096_000_000, 2_048_000_000, media, trim, bus, rotation);
        Assert.Equal(expected, Strings.For(indonesian).MediaFacts(volume));
    }

    [Theory]
    [InlineData(false, true, DriveOptimizerViewModel.TabSummary, DriveOptimizerViewModel.TabFragments)]
    [InlineData(true, true, DriveOptimizerViewModel.TabSummary, DriveOptimizerViewModel.TabSummary)]
    [InlineData(true, true, DriveOptimizerViewModel.TabFragments, DriveOptimizerViewModel.TabFragments)]
    [InlineData(true, false, DriveOptimizerViewModel.TabFragments, DriveOptimizerViewModel.TabSummary)]
    [InlineData(false, false, DriveOptimizerViewModel.TabFragments, DriveOptimizerViewModel.TabSummary)]
    public void Moving_to_a_hard_disk_opens_its_fragmented_files(bool wasHdd, bool isHdd, int current, int expected) =>
        Assert.Equal(expected, DriveOptimizerViewModel.TabFor(wasHdd, isHdd, current));

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(42.9, "42%")]
    [InlineData(100, "100%")]
    [InlineData(130, "100%")]
    [InlineData(double.NaN, "0%")]
    public void Progress_reads_as_a_whole_percentage(double value, string text) => Assert.Equal(text, DriveOptimizerViewModel.FormatPercent(value));

    [Fact]
    public void The_shell_shows_the_drive_optimizer_page()
    {
        string shell = Repo.Read("src", "Niskala.UI", "Views", "ShellView.xaml.cs");
        Assert.Contains("Section.Drives => new DriveOptimizerPage(_services.Drives)", shell, StringComparison.Ordinal);
        Assert.Contains("_services.Drives.EnsureLoaded();", shell, StringComparison.Ordinal);
    }
}
