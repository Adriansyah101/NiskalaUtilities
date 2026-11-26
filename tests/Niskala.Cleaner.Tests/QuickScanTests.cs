using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Core.Models;

namespace Niskala.Cleaner.Tests;

public sealed class QuickScanTests
{
    private const string Rules = """
        {
          "schemaVersion": 1,
          "rules": [
            { "id": "t.quick", "category": "temporaryFiles", "name": { "en": "Quick", "id": "Cepat" }, "quick": true, "targets": [ { "root": "userTemp" } ] },
            { "id": "t.slow", "category": "crashDumps", "name": { "en": "Slow", "id": "Lambat" }, "targets": [ { "root": "localAppData", "path": "Dumps" } ] }
          ],
          "browsers": [
            { "id": "alpha", "name": "Alpha", "engine": "chromium", "userData": [ { "root": "localAppData", "path": "Alpha\\User Data" } ] }
          ],
          "engines": [
            {
              "engine": "chromium", "profileMarker": "Preferences",
              "items": [
                { "id": "cache", "category": "browserCache", "name": { "en": "Cache", "id": "Cache" }, "quick": true, "targets": [ { "path": "Cache" } ] },
                { "id": "history", "category": "browserHistory", "name": { "en": "History", "id": "Riwayat" }, "removal": "recycleBin", "targets": [ { "files": ["History"] } ] }
              ],
              "databases": ["History"]
            }
          ]
        }
        """;

    [Fact]
    public void Quick_scan_sums_junk_and_browser_caches_and_lists_heavy_startup_items()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        scratch.File(@"UserTemp\a.tmp", 300);
        scratch.File(@"LocalAppData\Dumps\x.dmp", 9000);
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Cache\f_1", 200);
        scratch.File(@"LocalAppData\Alpha\User Data\Default\History", 5000);
        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("t.json", Rules)]);
        var startup = new FakeStartup(
            new StartupHindrance("a", "Light", "Run", BootImpact.Low, null),
            new StartupHindrance("b", "Heavy", "Run", BootImpact.High, "Acme"),
            new StartupHindrance("c", "Middle", "Service", BootImpact.Medium, null));

        Assert.Empty(catalog.Problems);
        QuickScanReport report = new QuickScanner(catalog, new CleanScanner(environment), startup).Scan(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["t.quick"], report.System.Select(p => p.Rule.Id));
        Assert.Equal(["browser.alpha.cache"], report.Browsers.Select(p => p.Rule.Id));
        Assert.Equal((300L, 200L), (report.SystemBytes, report.BrowserBytes));
        Assert.Equal(["Heavy", "Middle"], report.Startup.Select(s => s.Name));
        Assert.Equal(2, report.Cleanable.Count);
    }

    [Fact]
    public void Quick_scan_names_the_browsers_it_had_to_skip()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        environment.Running.Add("alpha");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Cache\f_1", 200);
        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("t.json", Rules.Replace("\"userData\"", "\"processes\": [\"alpha\"], \"userData\"", StringComparison.Ordinal))]);

        QuickScanReport report = new QuickScanner(catalog, new CleanScanner(environment), null).Scan(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha"], report.LockedBrowsers);
        Assert.Equal(0, report.BrowserBytes);
        Assert.Empty(report.Startup);
    }

    private sealed class FakeStartup(params StartupHindrance[] items) : IStartupReview
    {
        public IReadOnlyList<StartupHindrance> Review(CancellationToken cancellationToken = default) => items;
    }
}
