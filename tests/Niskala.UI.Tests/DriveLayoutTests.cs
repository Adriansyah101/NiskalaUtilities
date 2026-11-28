using System.Text.RegularExpressions;
using Niskala.Optimizer.Drives;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class DriveLayoutTests
{
    private static DriveVolume Volume(MediaKind media, StorageBus bus = StorageBus.Sata) =>
        new('E', "Backup", "NTFS", 4096, 1_000_000, 4_096_000_000, 2_048_000_000, media, media == MediaKind.Ssd, bus);

    private static string Page => Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml");

    private static string Model => Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");

    [Theory]
    [InlineData(false, "Drive", "Status", "Last analysis", "Total files", "Fragmented files", "Free-space fragments", "Fragmentation %", "Assessment", "Media", "File system", "Capacity")]
    [InlineData(true, "Drive", "Status", "Analisis terakhir", "Total berkas", "Berkas terfragmentasi", "Fragmen ruang kosong", "Fragmentasi %", "Penilaian", "Media", "Sistem berkas", "Kapasitas")]
    public void The_drive_table_has_every_column_in_both_languages(bool indonesian, params string[] headers)
    {
        Strings s = Strings.For(indonesian);
        string[] actual =
            [s.ColumnDrive, s.ColumnStatus, s.ColumnLastAnalysis, s.ColumnFiles, s.ColumnFragmentedFiles, s.ColumnFreeFragments, s.ColumnFragmentation, s.ColumnAssessment, s.ColumnMedia, s.ColumnFileSystem, s.ColumnCapacity];
        Assert.Equal(headers, actual);
    }

    [Theory]
    [InlineData(false, "Tidy", "Slightly fragmented", "Heavily fragmented")]
    [InlineData(true, "Rapi", "Sedikit terfragmentasi", "Sangat terfragmentasi")]
    public void Assessments_read_as_plain_words(bool indonesian, string tidy, string slightly, string heavily)
    {
        Strings s = Strings.For(indonesian);
        Assert.Equal([tidy, slightly, heavily], Enum.GetValues<DriveAssessment>().Select(s.AssessmentName));
    }

    [Theory]
    [InlineData(false, false, ProbeState.Detecting, false, false, "Detecting…")]
    [InlineData(false, false, ProbeState.Waiting, false, false, "Waiting for the drive…")]
    [InlineData(false, false, ProbeState.Failed, false, false, "No response")]
    [InlineData(false, false, ProbeState.Ready, false, false, "Not analyzed")]
    [InlineData(false, false, ProbeState.Ready, false, true, "Analyzed")]
    [InlineData(false, false, ProbeState.Ready, true, true, "Health warning")]
    [InlineData(false, true, ProbeState.Ready, true, true, "Working…")]
    [InlineData(true, false, ProbeState.Waiting, false, false, "Menunggu drive…")]
    [InlineData(true, false, ProbeState.Ready, true, false, "Peringatan kesehatan")]
    public void The_status_column_follows_the_probe_the_run_and_the_health(bool indonesian, bool busy, ProbeState probe, bool warning, bool analyzed, string expected) =>
        Assert.Equal(expected, DriveRow.StatusText(Strings.For(indonesian), busy, probe, warning, analyzed));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_schedule_has_a_name_and_an_explanation(bool indonesian)
    {
        Strings s = Strings.For(indonesian);
        Assert.Equal(4, Enum.GetValues<MaintenanceSchedule>().Select(s.ScheduleName).Distinct().Count());
        foreach (MaintenanceSchedule schedule in Enum.GetValues<MaintenanceSchedule>())
        {
            string note = s.ScheduleNote(new ScheduleState(schedule, null, 0));
            Assert.False(string.IsNullOrWhiteSpace(note));
            Assert.Equal(schedule != MaintenanceSchedule.Off, note.Contains("TRIM", StringComparison.Ordinal));
        }

        Assert.Contains("0x80070005", s.ScheduleNote(new ScheduleState(MaintenanceSchedule.Off, null, unchecked((int)0x80070005))), StringComparison.Ordinal);
        Assert.Contains("Task Scheduler", s.ScheduleNote(new ScheduleState(MaintenanceSchedule.Weekly, new DateTime(2026, 11, 29, 3, 0, 0), 0)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, HealthProtocol.Sata, true, "Western Digital (1058:2626)", "SATA / SAT · USB bridge Western Digital (1058:2626)")]
    [InlineData(true, HealthProtocol.Nvme, true, "JMicron (152D:0583)", "NVMe · bridge USB JMicron (152D:0583)")]
    [InlineData(false, HealthProtocol.Nvme, false, null, "NVMe")]
    [InlineData(false, HealthProtocol.Unknown, true, null, "Unknown · USB bridge")]
    public void The_interface_row_names_the_protocol_and_the_bridge(bool indonesian, HealthProtocol protocol, bool bridged, string? bridge, string expected)
    {
        var health = new DriveHealth(HealthSource.SatPassThrough, HealthWarning.None, 30, 1, null, null, bridged, protocol, bridge);
        Assert.Equal(expected, Strings.For(indonesian).InterfaceText(health));
    }

    [Fact]
    public void A_usb_hard_disk_read_through_sat_shows_its_interface_model_sectors_and_attributes()
    {
        var health = new DriveHealth(
            HealthSource.SatPassThrough,
            HealthWarning.PendingSectors,
            37,
            427,
            null,
            null,
            Bridged: true,
            Protocol: HealthProtocol.Sata,
            Bridge: "Western Digital (1058:2626)",
            Model: "WDC WD50NDZW-11BCSS0",
            Attributes: [new AtaAttribute(197, 200, 200, 0, 337), new AtaAttribute(5, 140, 140, 140, 12)],
            ReallocatedSectors: 12,
            PendingSectors: 337,
            UncorrectableSectors: 0);

        IReadOnlyList<SummaryRow> rows = DriveOptimizerViewModel.HealthRows(Strings.For(false), Volume(MediaKind.Hdd, StorageBus.Usb), health);

        Assert.Equal(
            ["Physical health", "Attention", "Temperature", "Power-on hours", "Reallocated / pending / uncorrectable", "Interface", "Model", "Read from"],
            rows.Select(r => r.Label));
        Assert.Equal("12 / 337 / 0", rows[4].Value);
        Assert.Equal("SATA / SAT · USB bridge Western Digital (1058:2626)", rows[5].Value);
        Assert.Equal("WDC WD50NDZW-11BCSS0", rows[6].Value);
        Assert.Equal("S.M.A.R.T. through the USB bridge (SAT pass-through)", rows[^1].Value);

        var pending = new AttributeRow(health.Table[0]);
        var failing = new AttributeRow(health.Table[1]);
        Assert.Equal(("C5", "Pending sectors", "337", "OK"), (pending.Id, pending.Name, pending.Raw, pending.State));
        Assert.Equal(("05", "Failing", true), (failing.Id, failing.State, failing.IsFailing));
    }

    [Fact]
    public void Nvme_counters_add_traffic_and_shutdown_rows()
    {
        var health = new DriveHealth(HealthSource.NvmeBridge, HealthWarning.None, 45, 1_234, 3, 100, Bridged: true, Protocol: HealthProtocol.Nvme, Counters: new NvmeCounters(4_000_000, 1_000_000, 35, 77, 0, 4));

        IReadOnlyList<SummaryRow> rows = DriveOptimizerViewModel.HealthRows(Strings.For(false), Volume(MediaKind.Ssd, StorageBus.Usb), health);

        Assert.Contains(rows, r => r.Label == "Data read" && r.Value.Contains("TB", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Label == "Unsafe shutdowns" && r.Value == "77");
        Assert.Contains(rows, r => r.Label == "Power cycles" && r.Value == "35");
        Assert.Contains(rows, r => r.Label == "Interface" && r.Value.StartsWith("NVMe", StringComparison.Ordinal));
        Assert.Equal("NVMe health log through the USB bridge", rows[^1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_known_attribute_has_a_name_and_unknown_ones_are_vendor_specific(bool indonesian)
    {
        Strings s = Strings.For(indonesian);
        byte[] known = [1, 3, 4, 5, 7, 9, 10, 12, 177, 187, 188, 190, 193, 194, 196, 197, 198, 199, 231, 233, 241, 242];
        Assert.All(known, id => Assert.NotEqual(s.AttributeTitle(0xFE), s.AttributeTitle(id)));
        Assert.Equal(s.AttributeTitle(0xFE), s.AttributeTitle(0xFD));
    }

    [Fact]
    public void Ssds_see_every_relocation_strategy_as_not_advisable()
    {
        Assert.Equal("Not advisable", Strings.For(false).FitName(StrategyFit.NotApplicable));
        Assert.Equal("Tidak disarankan", Strings.For(true).FitName(StrategyFit.NotApplicable));
        Assert.Equal("Apply recommendation", Strings.For(false).GuideApplyRecommendation);
        Assert.Equal("Terapkan rekomendasi", Strings.For(true).GuideApplyRecommendation);
    }

    [Fact]
    public void The_page_puts_the_drive_table_on_top_and_the_views_below()
    {
        string page = Page;
        int table = page.IndexOf("x:Name=\"DriveList\"", StringComparison.Ordinal);
        int tabs = page.IndexOf("x:Name=\"Tabs\"", StringComparison.Ordinal);
        Assert.True(table > 0 && tabs > table, "The drive table must come before the view switcher.");
        foreach (string column in (string[])["ColumnDrive", "ColumnStatus", "ColumnLastAnalysis", "ColumnFiles", "ColumnFragmentedFiles", "ColumnFreeFragments", "ColumnFragmentation", "ColumnAssessment", "ColumnMedia", "ColumnFileSystem", "ColumnCapacity"])
        {
            Assert.Contains($"{{x:Bind ViewModel.S.{column}}}", page, StringComparison.Ordinal);
        }

        foreach (string cell in (string[])["Status", "LastAnalysis", "TotalFiles", "FragmentedFiles", "FreeFragments", "Fragmentation", "Assessment", "Media", "FileSystem", "UsedPercent"])
        {
            Assert.Contains($"{{x:Bind {cell}, Mode=OneWay}}", page, StringComparison.Ordinal);
        }

        Assert.Equal(2, Regex.Matches(page, "<ColumnDefinition Width=\"3\\*\" />\\s+<ColumnDefinition Width=\"2\\*\" />\\s+<ColumnDefinition Width=\"2\\*\" />").Count);
        Assert.Contains("x:Name=\"MapTab\"", page, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FragmentsTab\"", page, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SummaryTab\"", page, StringComparison.Ordinal);
        Assert.Contains("IsActive=\"{x:Bind IsDetecting, Mode=OneWay}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_strategy_guide_is_a_right_drawer_with_three_goals_a_main_card_and_alternatives()
    {
        string page = Page;
        int drawer = page.IndexOf("x:Name=\"GuideDrawer\" Grid.Column=\"1\"", StringComparison.Ordinal);
        Assert.True(drawer > 0);
        string tail = page[drawer..];
        Assert.Contains("Visibility=\"{x:Bind services:Format.Visible(ViewModel.IsGuideOpen), Mode=OneWay}\"", tail, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.IsSpeedGoal, Mode=TwoWay}\"", tail, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.IsImpactGoal, Mode=TwoWay}\"", tail, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.IsReclaimGoal, Mode=TwoWay}\"", tail, StringComparison.Ordinal);
        Assert.True(tail.IndexOf("ViewModel.GuideMainFit", StringComparison.Ordinal) < tail.IndexOf("ViewModel.Strategies", StringComparison.Ordinal));
        Assert.Contains("x:Name=\"ApplyGuideButton\"", tail, StringComparison.Ordinal);
        Assert.Contains("Content=\"{x:Bind ViewModel.S.GuideApplyRecommendation}\"", tail, StringComparison.Ordinal);
        Assert.Contains("Command=\"{x:Bind ViewModel.CloseGuideCommand}\"", tail, StringComparison.Ordinal);

        string model = Model;
        Assert.Contains("GuideMainTitle = S.DriveTrim;", model, StringComparison.Ordinal);
        Assert.Contains("GuideGoal = advice.Recommended ?? StrategyGuide.GoalFor(Mode);", model, StringComparison.Ordinal);
        Assert.Contains("return TrimAsync(row);", model, StringComparison.Ordinal);
        Assert.Contains("ApplyStrategy(GuideGoal);\r\n        return DefragmentAsync(row, Mode, null);", model, StringComparison.Ordinal);
        Assert.Contains("(row.IsSsd ? row.Volume.TrimSupported : row.Volume.CanRelocate)", model, StringComparison.Ordinal);

        string code = Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml.cs");
        Assert.Contains("if (!ViewModel.GuideMovesClusters)", code, StringComparison.Ordinal);
        Assert.Contains("Confirm.Ask(ApplyGuideButton, Strings.Current.DriveDefragConfirm", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_map_has_its_own_colors_for_reserved_and_compressed_clusters_in_both_themes()
    {
        string page = Page;
        Assert.Contains("ReservedBrush=\"{ThemeResource NiskalaMapReservedBrush}\"", page, StringComparison.Ordinal);
        Assert.Contains("CompressedBrush=\"{ThemeResource NiskalaMapCompressedBrush}\"", page, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ViewModel.S.LegendReserved}", page, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ViewModel.S.LegendCompressed}", page, StringComparison.Ordinal);

        string app = Repo.Read("src", "Niskala.UI", "App.xaml");
        Assert.Equal(2, Regex.Matches(app, "x:Key=\"NiskalaMapReservedBrush\"").Count);
        Assert.Equal(2, Regex.Matches(app, "x:Key=\"NiskalaMapCompressedBrush\"").Count);

        string view = Repo.Read("src", "Niskala.UI", "Views", "DriveMapView.cs");
        Assert.Contains("Pixel(SystemBrush), Pixel(ReservedBrush), Pixel(CompressedBrush)", view, StringComparison.Ordinal);
        Assert.Equal((int)ClusterState.MftReserved, 5);
        Assert.Equal((int)ClusterState.Compressed, 6);
    }

    [Fact]
    public void The_page_opens_from_the_fast_drive_list_and_probes_each_drive_in_the_background()
    {
        string model = Model;
        Assert.Contains("volumes = await Task.Run(_loader.Value.Shell);", model, StringComparison.Ordinal);
        Assert.Contains("_ = ProbeAsync(volumes, ask, probe.Token);", model, StringComparison.Ordinal);
        Assert.Contains("if (ask != _listAsk || Drives.FirstOrDefault(d => d.Volume.Letter == update.Volume.Letter) is not { } row)", model, StringComparison.Ordinal);
        Assert.Contains("_optimizer.Value.Media.ForgetHealth();", model, StringComparison.Ordinal);
        Assert.Contains("private bool CanAnalyze() => !IsRunning && Selected is { CanAnalyze: true };", model, StringComparison.Ordinal);
        Assert.DoesNotContain("_optimizer.Value.Volumes()", model, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schedule_box_registers_through_the_scheduler_only_after_it_was_read()
    {
        string page = Page;
        Assert.Contains("SelectedIndex=\"{x:Bind ViewModel.ScheduleIndex, Mode=TwoWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanChangeSchedule, Mode=OneWay}\"", page, StringComparison.Ordinal);

        string model = Model;
        Assert.Contains("if (_scheduleSync || !_scheduleLoaded || value < 0 || (MaintenanceSchedule)value == _schedule.Schedule)", model, StringComparison.Ordinal);
        Assert.Contains("ScheduledMaintenance.CanSchedule(Environment.ProcessPath, ScheduledMaintenance.InstalledRoots())", model, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => _scheduler.Value.Apply(schedule, program));", model, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheduled_start_runs_before_any_window_is_created()
    {
        string program = Repo.Read("src", "Niskala.UI", "Program.cs");
        int quiet = program.IndexOf("ScheduledMaintenance.IsScheduledRun(args)", StringComparison.Ordinal);
        int window = program.IndexOf("XamlGeneratedProgram.XamlGeneratedMain();", StringComparison.Ordinal);
        Assert.True(quiet > 0 && window > quiet);
        Assert.Contains("ScheduledOptimization.RunAsync(NiskalaHost.CreateDriveOptimizer(), null, CancellationToken.None)", program, StringComparison.Ordinal);
        Assert.Contains("new Mutex(initiallyOwned: true, ScheduledOptimization.MutexName, out bool owned)", program, StringComparison.Ordinal);
        Assert.Contains("<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>", Repo.Read("src", "Niskala.UI", "Niskala.UI.csproj"), StringComparison.Ordinal);
        Assert.Equal(typeof(Niskala.UI.Program), typeof(DriveOptimizerViewModel).Assembly.EntryPoint?.DeclaringType);
    }
}
