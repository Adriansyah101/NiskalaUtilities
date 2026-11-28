using Niskala.Optimizer.Drives;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class DriveStoppingTests
{
    [Theory]
    [InlineData(false, "Stopping after current file…")]
    [InlineData(true, "Menghentikan setelah berkas aktif selesai…")]
    public void Stopping_says_the_current_file_is_finished_first(bool indonesian, string text) =>
        Assert.Equal(text, Strings.For(indonesian).DriveStopping);

    [Theory]
    [InlineData(false, DrivePhase.Stopping, "Finishing E:\\a.dat")]
    [InlineData(true, DrivePhase.Stopping, "Menyelesaikan E:\\a.dat")]
    [InlineData(false, DrivePhase.ReadingFileTable, "Reading the file table…")]
    [InlineData(true, DrivePhase.ReadingFileTable, "Membaca tabel berkas…")]
    public void The_new_phases_have_text_in_both_languages(bool indonesian, DrivePhase phase, string text) =>
        Assert.Equal(text, Strings.For(indonesian).DrivePhaseText(phase, "E:\\a.dat"));

    [Fact]
    public void Every_phase_but_finished_has_text()
    {
        foreach (DrivePhase phase in Enum.GetValues<DrivePhase>().Where(p => p != DrivePhase.Finished))
        {
            Assert.NotEmpty(Strings.For(false).DrivePhaseText(phase, "E:"));
            Assert.NotEmpty(Strings.For(true).DrivePhaseText(phase, "E:"));
        }
    }

    [Theory]
    [InlineData(false, false, "Analyze")]
    [InlineData(false, true, "Re-analyze")]
    [InlineData(true, false, "Analisis")]
    [InlineData(true, true, "Analisis ulang")]
    public void The_analyze_button_offers_a_fresh_scan_once_a_result_is_kept(bool indonesian, bool analyzed, string label) =>
        Assert.Equal(label, DriveOptimizerViewModel.AnalyzeLabel(Strings.For(indonesian), analyzed));

    [Fact]
    public void Cancel_during_a_move_shows_the_stopping_banner_until_the_run_ends()
    {
        string model = Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");
        Assert.Contains("_cancel?.Cancel();\r\n        if (_moving)\r\n        {\r\n            ShowStopping();", model, StringComparison.Ordinal);
        Assert.Contains("Banner.Show(InfoBarSeverity.Informational, S.DriveStopping);", model, StringComparison.Ordinal);
        Assert.Contains("if (progress.Phase == DrivePhase.Stopping && IsRunning)", model, StringComparison.Ordinal);
        Assert.Contains("_moving = true;\r\n        DefragResult result = await _optimizer.Value.DefragmentAsync(", model, StringComparison.Ordinal);
        Assert.Contains("_moving = false;\r\n            IsStopping = false;", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Results_come_from_the_session_cache_and_the_button_is_bound_to_its_label()
    {
        string model = Repo.Read("src", "Niskala.UI", "ViewModels", "DriveOptimizerViewModel.cs");
        Assert.DoesNotContain("_analyses", model, StringComparison.Ordinal);
        Assert.Contains("_optimizer.Value.Cached(volume)", model, StringComparison.Ordinal);
        Assert.Contains("Cached(row.Volume) ?? await AnalyzeCoreAsync(row, token)", model, StringComparison.Ordinal);
        string page = Repo.Read("src", "Niskala.UI", "Views", "DriveOptimizerPage.xaml");
        Assert.Contains("<TextBlock Text=\"{x:Bind ViewModel.AnalyzeText, Mode=OneWay}\" />", page, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{x:Bind ViewModel.AnalyzeText, Mode=OneWay}\"", page, StringComparison.Ordinal);
    }
}
