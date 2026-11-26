using Niskala.Core.Models;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Inventory.Msi;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class InventoryDeduplicatorTests
{
    private static readonly Guid Code = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static IReadOnlyList<InstalledApp> Registry(params InstalledApp[] apps) => InventoryDeduplicator.Apply(apps, [], []);

    private static InstalledApp Package(string family, string version, string id = "") => App(id.Length > 0 ? id : $"appx:{family}:{version}", "Acme App", version) with
    {
        Sources = AppSources.Appx,
        Scope = InstallScope.User,
        View = RegistryViewKind.None,
        InstallerKind = InstallerKind.Msix,
        Package = new PackageDetails($"{family}_{version}", family, PackageSignatureKind.Store, null, null, false),
    };

    [Fact]
    public void Same_product_code_in_both_views_keeps_the_64_bit_entry()
    {
        IReadOnlyList<InstalledApp> result = Registry(
            App("reg:hklm32:x") with { View = RegistryViewKind.Registry32, MsiProductCode = Code },
            App("reg:hklm64:x") with { MsiProductCode = Code });

        Assert.Equal(HiddenReason.Duplicate, result[0].HiddenReason);
        Assert.Equal("reg:hklm64:x", result[0].DuplicateOfId);
        Assert.True(result[1].IsVisible);
    }

    [Fact]
    public void Identical_name_version_and_command_are_duplicates()
    {
        IReadOnlyList<InstalledApp> result = Registry(
            App("a") with { UninstallString = "x.exe /u", InstallLocation = @"C:\Acme" },
            App("b") with { View = RegistryViewKind.Registry32, UninstallString = "x.exe /u" });

        Assert.True(result[0].IsVisible);
        Assert.Equal(HiddenReason.Duplicate, result[1].HiddenReason);
    }

    [Fact]
    public void Same_name_with_different_commands_is_kept()
    {
        IReadOnlyList<InstalledApp> result = Registry(
            App("a") with { UninstallString = @"C:\Program Files\Acme\u.exe" },
            App("b") with { View = RegistryViewKind.Registry32, UninstallString = @"C:\Program Files (x86)\Acme\u.exe" });

        Assert.All(result, a => Assert.True(a.IsVisible));
    }

    [Fact]
    public void Already_hidden_entries_are_not_compared()
    {
        IReadOnlyList<InstalledApp> result = Registry(
            App("a") with { MsiProductCode = Code, HiddenReason = HiddenReason.SystemComponent },
            App("b") with { MsiProductCode = Code });

        Assert.Equal(HiddenReason.SystemComponent, result[0].HiddenReason);
        Assert.True(result[1].IsVisible);
    }

    [Fact]
    public void Msi_product_fills_gaps_of_its_uninstall_entry()
    {
        var product = new MsiProduct(Code, InstallScope.Machine, "Acme", "2.0", "Acme Inc", @"C:\Program Files\Acme", new DateOnly(2026, 1, 2));
        IReadOnlyList<InstalledApp> result = InventoryDeduplicator.Apply(
            [App("reg:hklm64:x", version: null) with { MsiProductCode = Code, InstallDateOrigin = InstallDateOrigin.KeyLastWrite }],
            [product],
            []);

        InstalledApp app = Assert.Single(result);
        Assert.Equal(AppSources.Registry | AppSources.Msi, app.Sources);
        Assert.Equal("2.0", app.DisplayVersion);
        Assert.Equal("Acme Inc", app.Publisher);
        Assert.Equal(@"C:\Program Files\Acme", app.InstallLocation);
        Assert.Equal(InstallLocationOrigin.MsiProperty, app.InstallLocationOrigin);
        Assert.Equal(new DateOnly(2026, 1, 2), app.InstallDate);
        Assert.Equal(InstallDateOrigin.MsiProperty, app.InstallDateOrigin);
    }

    [Fact]
    public void Registry_values_win_over_msi_properties()
    {
        var product = new MsiProduct(Code, InstallScope.Machine, "Acme", "2.0", "Other", @"C:\Other", new DateOnly(2020, 1, 1));
        InstalledApp app = Assert.Single(InventoryDeduplicator.Apply(
            [App("r") with { MsiProductCode = Code, Publisher = "Acme Inc", InstallLocation = @"C:\Acme", InstallDate = new DateOnly(2026, 5, 5), InstallDateOrigin = InstallDateOrigin.Registry }],
            [product],
            []));

        Assert.Equal("1.0", app.DisplayVersion);
        Assert.Equal("Acme Inc", app.Publisher);
        Assert.Equal(@"C:\Acme", app.InstallLocation);
        Assert.Equal(new DateOnly(2026, 5, 5), app.InstallDate);
    }

    [Fact]
    public void Msi_product_without_uninstall_entry_is_kept_hidden()
    {
        var product = new MsiProduct(Code, InstallScope.User, "Orphaned Acme", "1.0", null, null, null);
        InstalledApp app = Assert.Single(InventoryDeduplicator.Apply([], [product, product], []));

        Assert.Equal("msi:{11111111-2222-3333-4444-555555555555}", app.Id);
        Assert.Equal(AppSources.Msi, app.Sources);
        Assert.Equal(InstallerKind.Msi, app.InstallerKind);
        Assert.Equal(HiddenReason.NoUninstallEntry, app.HiddenReason);
        Assert.Equal(InstallScope.User, app.Scope);
    }

    [Fact]
    public void Package_family_keeps_the_highest_version()
    {
        IReadOnlyList<InstalledApp> result = InventoryDeduplicator.Apply(
            [],
            [],
            [Package("Acme.App_x", "1.2.0.0"), Package("Acme.App_x", "1.10.0.0"), Package("Other_y", "1.0.0.0")]);

        Assert.Equal(HiddenReason.Duplicate, result[0].HiddenReason);
        Assert.Equal(result[1].Id, result[0].DuplicateOfId);
        Assert.True(result[1].IsVisible);
        Assert.True(result[2].IsVisible);
    }

    [Fact]
    public void Sources_are_combined_in_order()
    {
        IReadOnlyList<InstalledApp> result = InventoryDeduplicator.Apply(
            [App("reg:a")],
            [new MsiProduct(Code, InstallScope.Machine, "Only MSI", null, null, null, null)],
            [Package("Acme.App_x", "1.0.0.0", "appx:Acme.App_x")]);

        Assert.Equal(["reg:a", "msi:{11111111-2222-3333-4444-555555555555}", "appx:Acme.App_x"], result.Select(a => a.Id));
    }
}
