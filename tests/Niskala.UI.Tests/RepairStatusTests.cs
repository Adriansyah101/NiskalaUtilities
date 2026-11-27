using Niskala.Repair;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class RepairStatusTests
{
    private const string Elapsed = "0:00:48";

    [Theory]
    [InlineData(false, "Finished · 0:00:48")]
    [InlineData(true, "Selesai · 0:00:48")]
    public void A_normal_finish_shows_only_the_word_and_the_time(bool indonesian, string expected)
    {
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(RepairOutcome.Succeeded, RepairDiagnostic.None, 0), Elapsed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_chkdsk_finish_with_a_nonzero_success_code_shows_no_code(bool indonesian)
    {
        string text = Strings.For(indonesian).RepairStatus(Result(RepairOutcome.Succeeded, RepairDiagnostic.None, 2), Elapsed);
        Assert.Equal(indonesian ? "Selesai · 0:00:48" : "Finished · 0:00:48", text);
    }

    [Theory]
    [InlineData(false, "Finished · 0:00:48. Restart Windows to complete it.")]
    [InlineData(true, "Selesai · 0:00:48. Mulai ulang Windows untuk menuntaskannya.")]
    public void A_reset_that_needs_a_restart_says_so_without_a_code(bool indonesian, string expected)
    {
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(RepairOutcome.RestartRequired, RepairDiagnostic.None, 0), Elapsed));
    }

    [Theory]
    [InlineData(RepairOutcome.Succeeded, RepairDiagnostic.None, 0)]
    [InlineData(RepairOutcome.RestartRequired, RepairDiagnostic.None, 0)]
    [InlineData(RepairOutcome.RestartRequired, RepairDiagnostic.None, RepairExitCodes.RestartRequired)]
    [InlineData(RepairOutcome.Cancelled, RepairDiagnostic.None, null)]
    [InlineData(RepairOutcome.IssuesFound, RepairDiagnostic.OfflineRepairNeeded, 3)]
    public void Runs_that_did_not_fail_carry_no_technical_code(RepairOutcome outcome, RepairDiagnostic diagnostic, int? code)
    {
        foreach (bool indonesian in new[] { false, true })
        {
            string text = Strings.For(indonesian).RepairStatus(Result(outcome, diagnostic, code), Elapsed);
            Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Exit code", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Kode keluar", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("error code", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kode galat", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Elapsed, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, "Failed · 0:00:48. DISM found no repair source files (0x800F081F). Check the internet connection and the Windows Update service, or choose install.wim or install.esd from a Windows ISO as a local repair source.")]
    [InlineData(true, "Gagal · 0:00:48. DISM tidak menemukan berkas sumber perbaikan (0x800F081F). Periksa koneksi internet dan layanan Windows Update, atau pilih install.wim atau install.esd dari ISO Windows sebagai sumber perbaikan lokal.")]
    public void A_known_failure_explains_itself_in_plain_words(bool indonesian, string expected)
    {
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(RepairOutcome.Failed, RepairDiagnostic.SourceFilesMissing, RepairExitCodes.SourceFilesMissing), Elapsed));
    }

    [Theory]
    [InlineData(false, "Failed · 0:00:48. Windows Update could not supply the repair files (0x800F0915): they were not found or could not be downloaded. Check the internet connection and the Windows Update service, or choose install.wim or install.esd from a Windows ISO as a local repair source.")]
    [InlineData(true, "Gagal · 0:00:48. Windows Update tidak dapat menyediakan berkas perbaikan (0x800F0915): berkasnya tidak ditemukan atau tidak dapat diunduh. Periksa koneksi internet dan layanan Windows Update, atau pilih install.wim atau install.esd dari ISO Windows sebagai sumber perbaikan lokal.")]
    public void A_repair_source_that_windows_update_cannot_supply_points_to_a_local_image(bool indonesian, string expected)
    {
        (RepairOutcome outcome, RepairDiagnostic diagnostic) = RepairExitCodes.Interpret(RepairCommandCatalog.Get(RepairCommandId.DismRestoreHealth), RepairExitCodes.RepairSourceNotFound);

        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(outcome, diagnostic, RepairExitCodes.RepairSourceNotFound), Elapsed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_refused_source_has_a_reason(bool indonesian)
    {
        Strings s = Strings.For(indonesian);
        foreach (RepairSourceProblem problem in Enum.GetValues<RepairSourceProblem>().Where(p => p != RepairSourceProblem.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(s.RepairSourceRefused(problem)));
        }

        Assert.Contains(@"D:\sources\install.wim", s.RepairSourceLocal(@"D:\sources\install.wim", 1), StringComparison.Ordinal);
        Assert.Contains("Windows Update", s.RepairSourceDefault, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, unchecked((int)0x800F0922), "Failed · 0:00:48. The command stopped with error code 0x800F0922.")]
    [InlineData(true, unchecked((int)0x800F0922), "Gagal · 0:00:48. Perintah berhenti dengan kode galat 0x800F0922.")]
    [InlineData(false, 1, "Failed · 0:00:48. The command stopped with error code 1.")]
    [InlineData(true, 1, "Gagal · 0:00:48. Perintah berhenti dengan kode galat 1.")]
    public void An_unknown_failure_names_its_code_once(bool indonesian, int code, string expected)
    {
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(RepairOutcome.Failed, RepairDiagnostic.None, code), Elapsed));
    }

    [Theory]
    [InlineData(false, "Could not start. The program is missing from System32.")]
    [InlineData(true, "Tidak bisa dijalankan. Programnya tidak ada di System32.")]
    public void A_missing_program_says_why_it_did_not_start(bool indonesian, string expected)
    {
        var result = new RepairResult(RepairCommandId.SfcScanNow, RepairOutcome.StartFailed, RepairDiagnostic.ProgramMissing, null, RepairExitCodes.FileNotFound, null, TimeSpan.Zero);
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(result, Elapsed));
    }

    [Theory]
    [InlineData(false, "Needs administrator rights, so nothing was started. Run Niskala as administrator.")]
    [InlineData(true, "Membutuhkan hak administrator, jadi tidak ada yang dijalankan. Jalankan Niskala sebagai administrator.")]
    public void A_run_without_elevation_says_what_to_do(bool indonesian, string expected)
    {
        Assert.Equal(expected, Strings.For(indonesian).RepairStatus(Result(RepairOutcome.NotElevated, RepairDiagnostic.ElevationRequired, null), Elapsed));
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(4.3, "4%")]
    [InlineData(42.0, "42%")]
    [InlineData(99.9, "99%")]
    [InlineData(100.0, "100%")]
    [InlineData(-3.0, "0%")]
    [InlineData(140.0, "100%")]
    [InlineData(double.NaN, "0%")]
    public void The_percentage_beside_the_bar_is_a_whole_number(double percent, string expected)
    {
        Assert.Equal(expected, RepairViewModel.FormatPercent(percent));
    }

    private static RepairResult Result(RepairOutcome outcome, RepairDiagnostic diagnostic, int? code) =>
        new(RepairCommandId.DismRestoreHealth, outcome, diagnostic, code, null, null, TimeSpan.FromSeconds(48));
}
