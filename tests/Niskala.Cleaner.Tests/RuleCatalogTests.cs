using Niskala.Cleaner.Rules;

namespace Niskala.Cleaner.Tests;

public sealed class RuleCatalogTests
{
    private const string Header = """{ "schemaVersion": 1, "rules": [ """;
    private const string Footer = " ] }";

    [Fact]
    public void Shipped_rules_load_without_problems()
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Load(Path.Combine(AppContext.BaseDirectory, CleanRuleCatalog.FolderName));

        Assert.Empty(catalog.Problems);
        Assert.Equal(catalog.Rules.Count, catalog.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(catalog.Rules, r => r.Id == "windows.user-temp" && r.Quick && r.MinimumAge == TimeSpan.FromHours(24));
        foreach (string browser in new[] { "edge", "chrome", "brave", "vivaldi", "opera", "firefox", "librewolf" })
        {
            Assert.Contains(catalog.Rules, r => r.Id == $"browser.{browser}.cache" && r.Quick);
            Assert.Contains(catalog.Rules, r => r.Id == $"browser.{browser}.compact" && r.Action == RuleAction.Compact && r.Databases.Count > 0);
        }

        Assert.All(catalog.Rules.Where(r => r.Category is CleanCategory.BrowserHistory or CleanCategory.BrowserCookies or CleanCategory.BrowserSessions), r =>
        {
            Assert.Equal(RemovalMode.RecycleBin, r.Removal);
            Assert.False(r.Quick);
        });
    }

    [Fact]
    public void Gecko_history_is_never_deleted_because_bookmarks_share_its_file()
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Load(Path.Combine(AppContext.BaseDirectory, CleanRuleCatalog.FolderName));

        Assert.DoesNotContain(
            catalog.Rules.Where(r => r.Browser?.Engine == BrowserEngine.Gecko && r.Action == RuleAction.Delete).SelectMany(r => r.Targets).SelectMany(t => t.Files),
            f => f.StartsWith("places", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp", "path": "C:\\Windows" } ] }""", "relative")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp", "path": "..\\up" } ] }""", "plain folder name")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp", "path": "a*b" } ] }""", "plain folder name")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "path": "x" } ] }""", "names a root")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "windows" } ] }""", "sub-folder")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp", "files": ["..\\x"] } ] }""", "file patterns")]
    [InlineData("""{ "id": "A B", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp" } ] }""", "lower-case")]
    [InlineData("""{ "id": "browser.x", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp" } ] }""", "reserved")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [] }""", "at least one target")]
    [InlineData("""{ "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "minimumAgeHours": -1, "targets": [ { "root": "userTemp" } ] }""", "negative")]
    public void Unsafe_rules_are_refused(string rule, string expected)
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("test.json", Header + rule + Footer)]);

        Assert.Empty(catalog.Rules);
        RuleProblem problem = Assert.Single(catalog.Problems);
        Assert.Contains(expected, problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "rules": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "rules": [ { "id": "a.b", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp", "deleteEverything": true } ] } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "rules": [ { "id": "a.b", "category": "nuclear", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp" } ] } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "rules": [ { "id": "a.b", "category": "temporaryFiles", "targets": [ { "root": "userTemp" } ] } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "rules": [], }""")]
    public void Malformed_files_are_refused_whole(string json)
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("bad.json", json)]);

        Assert.Empty(catalog.Rules);
        Assert.Single(catalog.Problems);
    }

    [Fact]
    public void A_rule_id_used_twice_keeps_the_first()
    {
        const string Rule = """{ "id": "dup.rule", "category": "temporaryFiles", "name": { "en": "x", "id": "x" }, "targets": [ { "root": "userTemp" } ] }""";

        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("one.json", Header + Rule + Footer), ("two.json", Header + Rule + Footer)]);

        Assert.Single(catalog.Rules);
        Assert.Equal("two.json", Assert.Single(catalog.Problems).Source);
    }

    [Fact]
    public void Browser_items_expand_per_browser_and_need_their_engine()
    {
        const string Json = """
            {
              "schemaVersion": 1,
              "browsers": [
                { "id": "alpha", "name": "Alpha", "engine": "chromium", "processes": ["alpha"], "userData": [ { "root": "localAppData", "path": "Alpha\\User Data" } ] },
                { "id": "beta", "name": "Beta", "engine": "gecko", "userData": [ { "root": "roamingAppData", "path": "Beta\\Profiles" } ] }
              ],
              "engines": [
                { "engine": "chromium", "profileMarker": "Preferences", "items": [ { "id": "cache", "category": "browserCache", "name": { "en": "Cache", "id": "Cache" }, "area": "cache", "targets": [ { "path": "Cache", "recurse": true } ] } ], "databases": ["History"] }
              ]
            }
            """;

        CleanRuleCatalog catalog = CleanRuleCatalog.Parse([("b.json", Json)]);

        Assert.Equal(["browser.alpha.cache", "browser.alpha.compact"], catalog.Rules.Select(r => r.Id));
        Assert.Contains("engine Gecko", Assert.Single(catalog.Problems).Message, StringComparison.Ordinal);
        Assert.Equal("Alpha: Cache", catalog.Rules[0].DisplayName(indonesian: false));
    }

    [Fact]
    public void Selectors_match_ids_prefixes_and_categories()
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Load(Path.Combine(AppContext.BaseDirectory, CleanRuleCatalog.FolderName));

        Assert.Equal(["windows.user-temp"], catalog.Select(["windows.user-temp"]).Select(r => r.Id));
        Assert.All(catalog.Select(["browser.firefox.*"]), r => Assert.StartsWith("browser.firefox.", r.Id, StringComparison.Ordinal));
        Assert.All(catalog.Select(["crashDumps"]), r => Assert.Equal(CleanCategory.CrashDumps, r.Category));
        Assert.Single(catalog.Select(["windows.user-temp", "windows.user-temp"]));
    }
}
