using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Firewall;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class StoreAppLeftoverTests
{
    [Fact]
    public void The_data_folder_of_a_removed_package_family_is_safe_to_remove()
    {
        using var scratch = new ScratchFolder();
        string packages = Path.Combine(scratch.Root, "Packages");
        string data = Path.GetDirectoryName(scratch.File(@"Packages\Acme.Photo_8wekyb3d8bbwe\LocalState\settings.dat", 12))!;
        scratch.File(@"Packages\Other.App_8wekyb3d8bbwe\LocalState\x.dat", 3);
        InstalledApp app = Packaged("Acme.Photo_8wekyb3d8bbwe");

        LeftoverReport report = Scanner(packages).Scan(app, Empty(app), [], TestContext.Current.CancellationToken);

        LeftoverItem item = Assert.Single(report.Items);
        Assert.Equal(Path.GetDirectoryName(data), item.Path);
        Assert.Equal(ConfidenceTier.Safe, item.Tier);
        Assert.Equal(LeftoverSignal.PackageFamily, Assert.Single(item.Reasons).Signal);
        Assert.Equal(1, item.Files);
    }

    [Theory]
    [InlineData(@"..\Other.App_8wekyb3d8bbwe")]
    [InlineData("..")]
    [InlineData("")]
    public void A_family_name_that_is_not_a_plain_folder_name_is_ignored(string family)
    {
        using var scratch = new ScratchFolder();
        scratch.File(@"Other.App_8wekyb3d8bbwe\x.dat", 3);
        string packages = Directory.CreateDirectory(Path.Combine(scratch.Root, "Packages")).FullName;
        InstalledApp app = Packaged(family);

        Assert.Empty(Scanner(packages).Scan(app, Empty(app), [], TestContext.Current.CancellationToken).Items);
    }

    private static InstalledApp Packaged(string family) => App("appx:" + family, "Qzx Photo") with
    {
        Sources = AppSources.Appx,
        InstallerKind = InstallerKind.Msix,
        Package = new PackageDetails($"{family}_1.0.0.0_x64__8wekyb3d8bbwe", family, PackageSignatureKind.Store, null, null, false),
    };

    private static Fingerprint Empty(InstalledApp app) => new() { AppId = app.Id, TakenAt = DateTimeOffset.UtcNow };

    private static LeftoverScanner Scanner(string packages)
    {
        var system = new FakeTraceSource();
        return new LeftoverScanner(
            ZGuard(),
            RegistryGuard.ForCurrentMachine(),
            new NoTrust(),
            new FileLeftoverProbe([], system),
            new RegistryLeftoverProbe(system, NullLogger<RegistryLeftoverProbe>.Instance),
            new FirewallLeftoverProbe(new NoRules()),
            new SteamLeftoverProbe(() => null),
            new ScheduledTaskLeftoverProbe(system),
            new PackageLeftoverProbe(packages),
            SystemCommandEnvironment.Instance,
            NullLogger<LeftoverScanner>.Instance);
    }

    private sealed class NoRules : IFirewallRuleSource
    {
        public int TryList(out List<FirewallRule> rules)
        {
            rules = [];
            return 0;
        }
    }

    private sealed class NoTrust : IFileTrustProbe
    {
        public ExclusionReason? Check(string path, bool isDirectory) => null;
    }
}
