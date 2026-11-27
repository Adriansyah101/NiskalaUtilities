using Niskala.Scanner.Leftovers;
using Niskala.Uninstall.Leftovers;

namespace Niskala.Uninstall.Tests;

public sealed class LeftoverStagesTests
{
    [Fact]
    public void Files_come_first_and_hand_over_to_the_registry()
    {
        LeftoverStages stages = LeftoverStages.Split(
        [
            Item(LeftoverKind.RegistryKey, @"HKCU\Software\Acme"),
            Item(LeftoverKind.Folder, @"C:\Program Files\Acme"),
            Item(LeftoverKind.FirewallRule, "Acme"),
            Item(LeftoverKind.File, @"C:\Users\Public\Desktop\Acme.lnk"),
            Item(LeftoverKind.RegistryValue, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Acme"),
            Item(LeftoverKind.ScheduledTask, @"\Acme Updater"),
        ]);

        Assert.Equal(LeftoverStage.Files, stages.First);
        Assert.Equal([LeftoverKind.Folder, LeftoverKind.File], stages.Files.Select(i => i.Kind));
        Assert.Equal([LeftoverKind.RegistryKey, LeftoverKind.FirewallRule, LeftoverKind.RegistryValue, LeftoverKind.ScheduledTask], stages.Registry.Select(i => i.Kind));
        Assert.Equal(LeftoverStage.Registry, stages.After(LeftoverStage.Files));
        Assert.Null(stages.After(LeftoverStage.Registry));
        Assert.Same(stages.Files, stages.Items(LeftoverStage.Files));
        Assert.Same(stages.Registry, stages.Items(LeftoverStage.Registry));
    }

    [Fact]
    public void No_files_goes_straight_to_the_registry()
    {
        LeftoverStages stages = LeftoverStages.Split([Item(LeftoverKind.RegistryKey, @"HKCU\Software\Acme")]);

        Assert.False(stages.HasFiles);
        Assert.Equal(LeftoverStage.Registry, stages.First);
        Assert.Null(stages.After(LeftoverStage.Registry));
    }

    [Fact]
    public void Files_alone_finish_after_the_file_stage()
    {
        LeftoverStages stages = LeftoverStages.Split([Item(LeftoverKind.Folder, @"C:\Program Files\Acme")]);

        Assert.Equal(LeftoverStage.Files, stages.First);
        Assert.False(stages.HasRegistry);
        Assert.Null(stages.After(LeftoverStage.Files));
    }

    [Fact]
    public void Nothing_found_has_no_stage_at_all()
    {
        LeftoverStages stages = LeftoverStages.Split([]);

        Assert.True(stages.IsEmpty);
        Assert.Null(stages.First);
        Assert.Null(stages.After(LeftoverStage.Files));
    }

    [Fact]
    public void Values_to_write_back_keep_the_registry_stage_open()
    {
        LeftoverStages stages = LeftoverStages.Split([Item(LeftoverKind.Folder, @"C:\Program Files\Acme")], reverts: 1);

        Assert.True(stages.HasRegistry);
        Assert.Empty(stages.Registry);
        Assert.Equal(LeftoverStage.Registry, stages.After(LeftoverStage.Files));
        Assert.Equal(LeftoverStage.Registry, LeftoverStages.Split([], reverts: 2).First);
    }

    [Theory]
    [InlineData(LeftoverKind.Folder, LeftoverStage.Files)]
    [InlineData(LeftoverKind.File, LeftoverStage.Files)]
    [InlineData(LeftoverKind.RegistryKey, LeftoverStage.Registry)]
    [InlineData(LeftoverKind.RegistryValue, LeftoverStage.Registry)]
    [InlineData(LeftoverKind.FirewallRule, LeftoverStage.Registry)]
    [InlineData(LeftoverKind.ScheduledTask, LeftoverStage.Registry)]
    public void Every_kind_belongs_to_one_stage(LeftoverKind kind, LeftoverStage stage)
    {
        Assert.Equal(stage, LeftoverStages.StageOf(kind));
    }

    private static LeftoverItem Item(LeftoverKind kind, string path, string? value = null) => new(kind, path, value, 90, ConfidenceTier.Safe, []);
}
