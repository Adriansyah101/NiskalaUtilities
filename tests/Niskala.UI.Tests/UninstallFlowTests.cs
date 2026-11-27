using Niskala.Scanner.Leftovers;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed class UninstallFlowTests
{
    public static TheoryData<FlowExit> Exits => [FlowExit.CloseButton, FlowExit.Backdrop, FlowExit.Escape, FlowExit.Navigate];

    [Theory]
    [MemberData(nameof(Exits))]
    public void Leaving_before_the_app_is_removed_cancels_the_uninstall(FlowExit exit)
    {
        var gate = new FlowGate();

        gate.Enter(FlowStage.Preparing);
        Assert.Equal(FlowExitResult.CancelFlow, gate.Request(exit));
        Assert.True(gate.CanCancel);

        gate.Enter(FlowStage.Removing);
        Assert.Equal(FlowExitResult.CancelFlow, gate.Request(exit));
        Assert.False(gate.IsLocked);
        Assert.True(gate.ShowsCloseButton);
    }

    [Fact]
    public void The_palette_opens_before_the_removal_and_cancels_only_when_it_navigates()
    {
        var gate = new FlowGate();
        gate.Enter(FlowStage.Preparing);

        Assert.Equal(FlowExitResult.Allow, gate.Request(FlowExit.Palette));
        Assert.Equal(FlowExitResult.CancelFlow, gate.Request(FlowExit.Navigate));
    }

    [Theory]
    [InlineData(FlowExit.CloseButton)]
    [InlineData(FlowExit.Backdrop)]
    [InlineData(FlowExit.Escape)]
    [InlineData(FlowExit.Navigate)]
    [InlineData(FlowExit.Palette)]
    public void After_the_app_is_removed_every_way_out_is_locked_until_the_leftovers_are_done(FlowExit exit)
    {
        var gate = new FlowGate();
        gate.Enter(FlowStage.Removing);

        gate.Enter(FlowStage.Cleanup);

        Assert.True(gate.IsLocked);
        Assert.False(gate.ShowsCloseButton);
        Assert.False(gate.CanCancel);
        Assert.Equal(FlowExitResult.Refuse, gate.Request(exit));

        gate.Enter(FlowStage.Finished);

        Assert.False(gate.IsLocked);
        Assert.True(gate.ShowsCloseButton);
        Assert.Equal(FlowExitResult.Allow, gate.Request(exit));
    }

    [Theory]
    [InlineData(FlowStage.Idle)]
    [InlineData(FlowStage.Finished)]
    public void Without_a_running_uninstall_nothing_is_held_back(FlowStage stage)
    {
        var gate = new FlowGate();
        gate.Enter(stage);

        foreach (FlowExit exit in Enum.GetValues<FlowExit>())
        {
            Assert.Equal(FlowExitResult.Allow, gate.Request(exit));
        }
    }

    [Fact]
    public void Entering_the_same_stage_twice_reports_no_change()
    {
        var gate = new FlowGate();

        Assert.True(gate.Enter(FlowStage.Cleanup));
        Assert.False(gate.Enter(FlowStage.Cleanup));
    }

    [Fact]
    public void A_locked_rail_refuses_other_pages_but_keeps_the_current_one()
    {
        var nav = new NavigationViewModel();
        int shown = 0;
        nav.Shown += _ => shown++;

        nav.IsLocked = true;

        Assert.False(nav.IsUnlocked);
        Assert.False(nav.Select(Section.Repair));
        Assert.False(nav.Select(Section.History));
        Assert.False(nav.Select(Section.Updates));
        Assert.True(nav.Select(Section.Apps));
        Assert.Equal(Section.Apps, nav.Selected);
        Assert.Equal(0, shown);

        nav.IsLocked = false;

        Assert.True(nav.Select(Section.Repair));
        Assert.Equal(Section.Repair, nav.Selected);
        Assert.Equal(1, shown);
    }

    [Fact]
    public void The_rail_asks_before_leaving_and_stays_when_told_no()
    {
        var nav = new NavigationViewModel();
        var asked = new List<Section>();
        bool allow = false;
        nav.CanLeave = section =>
        {
            asked.Add(section);
            return allow;
        };

        Assert.False(nav.Select(Section.Storage));
        Assert.Equal(Section.Apps, nav.Selected);

        allow = true;
        Assert.True(nav.Select(Section.Storage));
        Assert.Equal(Section.Storage, nav.Selected);
        Assert.Equal([Section.Storage, Section.Storage], asked);
    }

    [Fact]
    public void The_file_stage_shows_only_files_with_the_permanent_option()
    {
        var review = new LeftoverReviewViewModel();

        review.Load(Leftovers(), i => i.Tier == ConfidenceTier.Safe, ReviewScope.Files);

        Assert.False(review.ShowTabs);
        Assert.True(review.ShowPermanent);
        Assert.Equal(ReviewTab.Files, review.Tab);
        Assert.Equal(2, review.Total);
        Assert.Equal(0, review.RegistryCount);
        Assert.All(review.Checked, i => Assert.True(i.Kind is LeftoverKind.Folder or LeftoverKind.File));
        Assert.Equal(review.FilesTabText, review.ScopeTitle);
    }

    [Fact]
    public void The_registry_stage_shows_registry_and_firewall_entries_with_their_details()
    {
        var review = new LeftoverReviewViewModel();

        review.Load(Leftovers(), i => i.Tier == ConfidenceTier.Safe, ReviewScope.Registry);

        Assert.False(review.ShowTabs);
        Assert.False(review.ShowPermanent);
        Assert.Equal(ReviewTab.Registry, review.Tab);
        Assert.Equal(3, review.Total);
        Assert.Equal(0, review.FileCount);
        Assert.Equal(review.RegistryTabText, review.ScopeTitle);
        LeftoverTreeRow value = Assert.Single(review.Visible, r => r.HasValueName);
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", value.Location);
        Assert.Equal("Acme", value.ValueNameText);
        Assert.Equal(Strings.Current.RegistryPath, value.LocationLabel);
        Assert.NotEmpty(value.Reasons);
    }

    [Fact]
    public void Without_a_stage_the_review_keeps_its_tabs_for_force_remove()
    {
        var review = new LeftoverReviewViewModel();

        review.Load(Leftovers(), i => true);

        Assert.True(review.ShowTabs);
        Assert.True(review.ShowPermanent);
        Assert.Equal(5, review.Total);
    }

    [Theory]
    [InlineData(false, "Leftover files", "Leftover registry", "Clean files & continue", "Skip files", "Clean registry", "Finish without deleting")]
    [InlineData(true, "Berkas sisa", "Registri sisa", "Hapus berkas & lanjut", "Lewati berkas", "Hapus registri", "Selesai tanpa menghapus")]
    public void The_new_stages_and_their_buttons_speak_both_languages(bool indonesian, string files, string registry, string cleanFiles, string skipFiles, string cleanRegistry, string finish)
    {
        Strings s = Strings.For(indonesian);

        Assert.Equal(files, s.StepFiles);
        Assert.Equal(registry, s.StepRegistry);
        Assert.Equal(cleanFiles, s.CleanFilesContinue);
        Assert.Equal(skipFiles, s.SkipFiles);
        Assert.Equal(cleanRegistry, s.CleanRegistry);
        Assert.Equal(finish, s.FinishWithoutDeleting);
        Assert.Equal(indonesian ? "Jejak aplikasi" : "Fingerprint", s.StepFingerprint);
        Assert.Equal(indonesian ? "Proses aktif" : "Live process tree", s.StepProcessTree);
        Assert.False(string.IsNullOrWhiteSpace(s.LockNotice));
        Assert.False(string.IsNullOrWhiteSpace(s.NoLeftovers));
    }

    private static List<LeftoverItem> Leftovers() =>
    [
        Item(LeftoverKind.Folder, Path.Combine(Path.GetTempPath(), "niskala-ui-tests-missing", "Acme")),
        Item(LeftoverKind.File, Path.Combine(Path.GetTempPath(), "niskala-ui-tests-missing", "acme.log")),
        Item(LeftoverKind.RegistryKey, @"HKCU\Software\NiskalaUiTestsMissing\Acme"),
        Item(LeftoverKind.RegistryValue, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Acme"),
        Item(LeftoverKind.FirewallRule, "Acme"),
    ];

    private static LeftoverItem Item(LeftoverKind kind, string path, string? value = null) =>
        new(kind, path, value, 90, ConfidenceTier.Safe, [new LeftoverReason(LeftoverSignal.ExactName, 90, "named like the app")]);
}
