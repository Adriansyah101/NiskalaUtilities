using Microsoft.UI.Xaml.Controls;
using Niskala.Repair;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class RepairBannerTests
{
    [Fact]
    public void Running_dism_then_winsock_then_dns_shows_one_banner_with_text_each_time()
    {
        var banner = new StatusBanner();
        var opened = new List<(bool Open, string? Message)>();
        banner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusBanner.IsOpen))
            {
                opened.Add((banner.IsOpen, banner.Message));
            }
        };
        Strings s = Strings.For(indonesian: false);

        RepairResult dism = Result(RepairCommandId.DismRestoreHealth, RepairOutcome.Failed, RepairDiagnostic.RepairSourceNotFound, RepairExitCodes.RepairSourceNotFound);
        banner.Clear();
        banner.Show(dism, s.RepairStatus(dism, "0:03:10"));
        Assert.Equal(InfoBarSeverity.Error, banner.Severity);
        Assert.True(banner.OffersSource);
        Assert.Contains("0x800F0915", banner.Message, StringComparison.Ordinal);

        RepairResult winsock = Result(RepairCommandId.NetshWinsockReset, RepairOutcome.RestartRequired, RepairDiagnostic.None, 0);
        banner.Clear();
        Assert.False(banner.IsOpen);
        banner.Show(winsock, s.RepairStatus(winsock, "0:00:02"));
        Assert.Equal(InfoBarSeverity.Warning, banner.Severity);
        Assert.False(banner.OffersSource);

        RepairResult dns = Result(RepairCommandId.FlushDns, RepairOutcome.Succeeded, RepairDiagnostic.None, 0);
        banner.Clear();
        banner.Show(dns, s.RepairStatus(dns, "0:00:01"));
        Assert.Equal(InfoBarSeverity.Success, banner.Severity);
        Assert.StartsWith("Finished", banner.Message, StringComparison.Ordinal);

        Assert.All(opened.Where(o => o.Open), o => Assert.False(string.IsNullOrWhiteSpace(o.Message)));
        Assert.Equal(3, opened.Count(o => o.Open));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void A_banner_without_words_stays_closed(string? message)
    {
        var banner = new StatusBanner();
        banner.Show(InfoBarSeverity.Success, "Finished · 0:00:01");

        banner.Show(InfoBarSeverity.Error, message);

        Assert.False(banner.IsOpen);
        Assert.Equal(string.Empty, banner.Message);
        Assert.False(banner.OffersSource);
    }

    [Theory]
    [InlineData(RepairOutcome.Succeeded, InfoBarSeverity.Success)]
    [InlineData(RepairOutcome.RestartRequired, InfoBarSeverity.Warning)]
    [InlineData(RepairOutcome.IssuesFound, InfoBarSeverity.Warning)]
    [InlineData(RepairOutcome.Cancelled, InfoBarSeverity.Warning)]
    [InlineData(RepairOutcome.Failed, InfoBarSeverity.Error)]
    [InlineData(RepairOutcome.NotElevated, InfoBarSeverity.Error)]
    [InlineData(RepairOutcome.StartFailed, InfoBarSeverity.Error)]
    public void Each_outcome_has_one_colour(RepairOutcome outcome, InfoBarSeverity severity)
    {
        Assert.Equal(severity, StatusBanner.SeverityOf(outcome));
    }

    [Fact]
    public void Every_outcome_and_diagnostic_produces_words_in_both_languages()
    {
        foreach (bool indonesian in new[] { false, true })
        {
            Strings s = Strings.For(indonesian);
            foreach (RepairOutcome outcome in Enum.GetValues<RepairOutcome>())
            {
                foreach (RepairDiagnostic diagnostic in Enum.GetValues<RepairDiagnostic>())
                {
                    Assert.False(string.IsNullOrWhiteSpace(s.RepairStatus(Result(RepairCommandId.DismRestoreHealth, outcome, diagnostic, 1), "0:00:01")));
                }
            }
        }
    }

    [Fact]
    public void Only_a_component_store_failure_about_sources_offers_a_local_image()
    {
        var banner = new StatusBanner();

        banner.Show(Result(RepairCommandId.SfcScanNow, RepairOutcome.Failed, RepairDiagnostic.SourceFilesMissing, 1), "Failed");
        Assert.False(banner.OffersSource);

        banner.Show(Result(RepairCommandId.DismRestoreHealth, RepairOutcome.Failed, RepairDiagnostic.AccessDenied, 5), "Failed");
        Assert.False(banner.OffersSource);

        banner.Show(Result(RepairCommandId.DismRestoreHealth, RepairOutcome.Failed, RepairDiagnostic.SourceFilesMissing, RepairExitCodes.SourceFilesMissing), "Failed");
        Assert.True(banner.OffersSource);
    }

    private static RepairResult Result(RepairCommandId id, RepairOutcome outcome, RepairDiagnostic diagnostic, int? code) =>
        new(id, outcome, diagnostic, code, null, null, TimeSpan.FromSeconds(1));
}
