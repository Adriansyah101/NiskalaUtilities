using Niskala.Optimizer.Drives;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;
using Niskala.UI.Views;

namespace Niskala.UI.Tests;

public sealed class DriveToolsTests
{
    private static DriveVolume Volume(MediaKind media, StorageBus bus = StorageBus.Sata) =>
        new('D', "Data", "NTFS", 4096, 1_000_000, 4_096_000_000, 2_048_000_000, media, media == MediaKind.Ssd, bus);

    [Theory]
    [InlineData(false, "Maximum Speed", "Low Impact", "Reclaim Contiguous Space", "Strategy Guide")]
    [InlineData(true, "Kecepatan Maksimal", "Dampak Ringan", "Satukan Ruang Kosong", "Panduan Strategi")]
    public void The_strategy_goals_have_their_own_names_in_both_languages(bool indonesian, string speed, string impact, string space, string button)
    {
        Strings strings = Strings.For(indonesian);
        Assert.Equal(speed, strings.GoalName(StrategyGoal.MaximumSpeed));
        Assert.Equal(impact, strings.GoalName(StrategyGoal.LowImpact));
        Assert.Equal(space, strings.GoalName(StrategyGoal.ReclaimSpace));
        Assert.Equal(button, strings.GuideButton);
        Assert.All(Enum.GetValues<StrategyGoal>(), g => Assert.False(string.IsNullOrWhiteSpace(strings.GoalHint(g))));
        Assert.Equal(4, Enum.GetValues<StrategyFit>().Select(strings.FitName).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_verdict_reads_as_a_sentence_and_ssds_point_to_trim(bool indonesian)
    {
        Strings strings = Strings.For(indonesian);
        var summary = new DriveSummary(1_000_000, 400_000, 1_200, 34, 120, 12.5, 50_000, 100, 1_000, 100, 4, 2, 1);
        foreach (StrategyReason reason in Enum.GetValues<StrategyReason>())
        {
            var advice = new StrategyAdvice(StrategyVerdict.Recommended, reason, null, [], DriveAction.Defragment);
            Assert.False(string.IsNullOrWhiteSpace(strings.GuideVerdict(advice, Volume(MediaKind.Hdd), summary)), reason.ToString());
        }

        StrategyAdvice ssd = StrategyGuide.Advise(Volume(MediaKind.Ssd), summary);
        Assert.Contains("TRIM", strings.GuideVerdict(ssd, Volume(MediaKind.Ssd), summary), StringComparison.Ordinal);
        StrategyAdvice heavy = StrategyGuide.Advise(Volume(MediaKind.Hdd), summary);
        Assert.Contains("12.5%", strings.GuideVerdict(heavy, Volume(MediaKind.Hdd), summary).Replace(',', '.'), StringComparison.Ordinal);
    }

    [Fact]
    public void Nvme_telemetry_fills_the_health_tab()
    {
        var health = new DriveHealth(HealthSource.Nvme, HealthWarning.None, 44, 5_455, 4, 100);
        IReadOnlyList<SummaryRow> rows = DriveOptimizerViewModel.HealthRows(Strings.For(false), Volume(MediaKind.Ssd, StorageBus.Nvme), health);

        Assert.Equal(
            ["Physical health", "Temperature", "Power-on hours", "Wear level", "Available spare", "Read from"],
            rows.Select(r => r.Label));
        Assert.Equal("Healthy", rows[0].Value);
        Assert.Equal("44 °C", rows[1].Value);
        Assert.StartsWith("5,455 h", rows[2].Value, StringComparison.Ordinal);
        Assert.Equal("4% used · 96% life left", rows[3].Value);
        Assert.Equal("NVMe health log", rows[^1].Value);
    }

    [Fact]
    public void Warnings_are_spelled_out_and_hard_disks_show_no_wear_row()
    {
        var health = new DriveHealth(HealthSource.Ata, HealthWarning.ReallocatedSectors | HealthWarning.PendingSectors, 38, 20_000, 12, null);
        IReadOnlyList<SummaryRow> rows = DriveOptimizerViewModel.HealthRows(Strings.For(true), Volume(MediaKind.Hdd), health);

        Assert.Equal("Peringatan", rows[0].Value);
        Assert.Equal("Perhatian", rows[1].Label);
        Assert.Contains("dialihkan", rows[1].Value, StringComparison.Ordinal);
        Assert.Contains(" · ", rows[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Label == "Tingkat keausan");
    }

    [Theory]
    [InlineData(false, true, "USB bridge")]
    [InlineData(false, false, "does not report")]
    [InlineData(true, true, "Bridge USB")]
    [InlineData(true, false, "tidak melaporkan")]
    public void A_drive_without_telemetry_gets_a_plain_explanation(bool indonesian, bool bridged, string expected)
    {
        DriveHealth health = DriveHealth.Unavailable(Volume(MediaKind.Hdd, bridged ? StorageBus.Usb : StorageBus.Sata));
        Assert.Empty(DriveOptimizerViewModel.HealthRows(Strings.For(indonesian), Volume(MediaKind.Hdd), health));
        Assert.Contains(expected, Strings.For(indonesian).HealthUnavailable(health.Bridged), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_health_and_inspector_label_is_translated(bool indonesian)
    {
        Strings strings = Strings.For(indonesian);
        Assert.All(Enum.GetValues<HealthWarning>().Where(w => w != HealthWarning.None), w => Assert.False(string.IsNullOrWhiteSpace(strings.HealthWarningName(w))));
        Assert.Equal(3, Enum.GetValues<HealthStatus>().Select(strings.HealthStatusName).Distinct().Count());
        Assert.Equal(5, Enum.GetValues<HealthSource>().Select(strings.HealthSourceName).Distinct().Count());
        Assert.Equal(4, Enum.GetValues<OccupantState>().Select(strings.OccupantName).Distinct().Count());
        Assert.Equal(strings.LegendSystem, strings.OccupantName(OccupantState.SystemLocked));
        Assert.Equal(3, Enum.GetValues<QueueSkipReason>().Where(r => r != QueueSkipReason.None).Select(strings.QueueSkipName).Distinct().Count());
    }

    [Fact]
    public void The_inspector_title_names_the_range_size_and_free_share()
    {
        var inspection = new ClusterInspection(100, 200, 25, 5, 3, []);
        Assert.Equal("Clusters 100–199 · 400 KB · 3 files · 25% free", Strings.For(false).InspectorTitle(inspection, 4096));
    }

    [Fact]
    public void A_queue_summary_names_every_drive_and_what_happened_to_it()
    {
        DriveVolume ssd = Volume(MediaKind.Ssd) with { Letter = 'C' };
        DriveVolume hdd = Volume(MediaKind.Hdd);
        IReadOnlyList<QueueResult> results =
        [
            new(new QueueStep(ssd, QueueStepKind.Trim), QueueOutcome.Done, Trim: new TrimResult(TrimOutcome.Trimmed, TrimMethod.SystemRetrim, 1, 0, TimeSpan.Zero)),
            new(new QueueStep(hdd, QueueStepKind.Defragment), QueueOutcome.Done, Defrag: new DefragResult(DefragMode.Quick, 3, 2, 2, 0, 0, 0, 10, false, TimeSpan.Zero)),
            new(new QueueStep(ssd, QueueStepKind.Defragment), QueueOutcome.Refused),
        ];

        string text = Strings.For(false).QueueDone(results, "0:01:00");

        Assert.StartsWith("Queue finished in 0:01:00: 2 of 3 drives done.", text, StringComparison.Ordinal);
        Assert.Contains("C: TRIM sent", text, StringComparison.Ordinal);
        Assert.Contains("D: 2 joined, 2 moved", text, StringComparison.Ordinal);
        Assert.Contains("never moved on an SSD", text, StringComparison.Ordinal);
        Assert.Equal("[2/5] Scanning", Strings.For(false).QueueActivity(1, 5, "Scanning"));
    }

    [Theory]
    [InlineData(true, DriveOptimizerViewModel.TabFragments)]
    [InlineData(false, DriveOptimizerViewModel.TabSummary)]
    public void Leaving_a_drive_closes_its_inspector_tab(bool isHdd, int expected)
    {
        Assert.Equal(expected, DriveOptimizerViewModel.TabFor(true, isHdd, DriveOptimizerViewModel.TabInspector));
        Assert.Equal(expected, DriveOptimizerViewModel.TabFor(false, isHdd, DriveOptimizerViewModel.TabInspector));
    }

    [Fact]
    public void A_click_on_the_map_lands_on_the_cell_under_the_pointer()
    {
        MapGrid grid = MapGrid.For(110, 33, 1);

        Assert.Equal((10, 3), (grid.Columns, grid.Rows));
        Assert.Equal(0, grid.CellAt(0, 0, 1));
        Assert.Equal(1, grid.CellAt(11.5, 2, 1));
        Assert.Equal(12, grid.CellAt(25, 12, 1));
        Assert.Equal(-1, grid.CellAt(200, 2, 1));
        Assert.Equal(-1, grid.CellAt(-1, 2, 1));
        Assert.Equal(-1, default(MapGrid).CellAt(1, 1, 1));
        Assert.Equal(22, MapGrid.For(220, 66, 2).CellAt(25, 12, 2));
        Assert.Equal(20, MapGrid.For(220, 66, 2).Columns);
    }

    [Fact]
    public void Arrow_keys_step_through_the_map_and_stop_at_its_edges()
    {
        MapGrid grid = MapGrid.For(110, 33, 1);

        Assert.Equal(1, grid.Step(0, 1, 0));
        Assert.Equal(0, grid.Step(0, -1, 0));
        Assert.Equal(10, grid.Step(0, 0, 1));
        Assert.Equal(29, grid.Step(25, 0, 1));
        Assert.Equal(5, grid.Step(15, 0, -1));
        Assert.Equal(29, grid.Step(99, 0, 0));
        Assert.Equal(-1, default(MapGrid).Step(0, 1, 0));
    }

    [Fact]
    public void The_page_wires_the_guide_queue_inspector_and_telemetry()
    {
        string page = Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml");
        Assert.Contains("IsChecked=\"{x:Bind IsQueued, Mode=TwoWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("Command=\"{x:Bind ViewModel.AnalyzeQueueCommand}\"", page, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOptimizeQueue\"", page, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.IsGuideOpen, Mode=TwoWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.Strategies}\"", page, StringComparison.Ordinal);
        Assert.Contains("Command=\"{x:Bind Apply}\"", page, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind services:Format.Visible(ViewModel.GuideShowsTrim), Mode=OneWay}\"", page, StringComparison.Ordinal);
        Assert.Contains("HighlightBrush=\"{ThemeResource NiskalaAccentBrush}\"", page, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InspectorTab\"", page, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.Occupants}\"", page, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.Health}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding", page, StringComparison.Ordinal);

        string code = Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml.cs");
        Assert.Contains("Map.CellPicked += OnCellPicked;", code, StringComparison.Ordinal);
        Assert.Contains("Map.CellPicked -= OnCellPicked;", code, StringComparison.Ordinal);
        Assert.Contains("Map.Show(ViewModel.Summarize, ViewModel.HighlightCell)", code, StringComparison.Ordinal);
        Assert.Contains("if (!ViewModel.QueueMovesClusters)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_queue_is_planned_by_the_optimizer_so_ssds_keep_the_trim_only_guard()
    {
        string model = Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");
        Assert.Contains("DriveQueue.Plan(rows.Select(r => r.Volume), kind, Mode)", model, StringComparison.Ordinal);
        Assert.Contains("new DriveQueueRunner(_optimizer.Value).RunAsync(steps, new QueueSink(this), token)", model, StringComparison.Ordinal);
        Assert.Contains("if (IsRunning || Selected?.Volume.CanRelocate != true)", model, StringComparison.Ordinal);
        Assert.Contains("_optimizer.Value.ReadHealthAsync(row.Volume, CancellationToken.None)", model, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => ClusterInspector.Inspect(analysis, start, end))", model, StringComparison.Ordinal);
    }
}
