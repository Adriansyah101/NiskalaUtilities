using Niskala.Cleaner.Browsers;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;

namespace Niskala.Cleaner.Tests;

public sealed class CleanScannerTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-3);

    [Fact]
    public void Age_patterns_and_recursion_decide_what_a_rule_lists()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string temp = environment.Folder(CleanRoot.UserTemp);
        scratch.File(@"UserTemp\old.tmp", 100, Old);
        scratch.File(@"UserTemp\fresh.tmp", 100);
        scratch.File(@"UserTemp\keep.log", 100, Old);
        scratch.File(@"UserTemp\deep\nested\old.tmp", 50, Old);
        CleanRule rule = Fixtures.Rule("t.temp", new TargetDefinition { Root = CleanRoot.UserTemp, Files = ["*.tmp"], Exclude = ["keep*"], Recurse = true })
            with { MinimumAge = TimeSpan.FromHours(24) };

        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Ready, preview.State);
        Assert.Equal(
            [Path.Combine(temp, @"deep\nested\old.tmp"), Path.Combine(temp, "old.tmp")],
            preview.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(150, preview.Bytes);
    }

    [Fact]
    public void Junctions_inside_a_target_are_never_followed()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string outside = scratch.Dir("Elsewhere");
        scratch.File(@"Elsewhere\precious.doc", 10, Old);
        scratch.File(@"UserTemp\junk.tmp", 10, Old);
        scratch.Junction(@"UserTemp\link", outside);
        CleanRule rule = Fixtures.Rule("t.temp", new TargetDefinition { Root = CleanRoot.UserTemp, Recurse = true });

        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);

        Assert.Equal(["junk.tmp"], preview.Files.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void A_target_folder_that_is_itself_a_junction_is_skipped()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string outside = scratch.Dir("Elsewhere");
        scratch.File(@"Elsewhere\precious.doc", 10, Old);
        scratch.Junction(@"LocalAppData\Cache", outside);
        CleanRule rule = Fixtures.Rule("t.cache", new TargetDefinition { Root = CleanRoot.LocalAppData, Path = "Cache", Recurse = true });

        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Empty, preview.State);
        Assert.Empty(preview.Folders);
    }

    [Fact]
    public void Elevated_rules_are_not_scanned_without_elevation()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch, elevated: false);
        scratch.File(@"WindowsTemp\junk.tmp", 10, Old);
        CleanRule rule = Fixtures.Rule("t.system", new TargetDefinition { Root = CleanRoot.WindowsTemp }) with { RequiresElevation = true };

        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.NeedsElevation, preview.State);
        Assert.Empty(preview.Files);
    }

    [Fact]
    public void Chromium_profiles_are_found_and_skipped_while_their_lock_is_held()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        BrowserDefinition browser = Chromium();
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Cache\data_1", 64, Old);
        scratch.File(@"LocalAppData\Alpha\User Data\Profile 1\Preferences");
        scratch.File(@"LocalAppData\Alpha\User Data\Profile 1\Cache\data_1", 32, Old);
        scratch.Dir(@"LocalAppData\Alpha\User Data\Crashpad");
        string lockFile = scratch.File(@"LocalAppData\Alpha\User Data\lockfile");
        CleanRule rule = CacheRule(browser);

        IReadOnlyList<BrowserProfile> profiles = BrowserProfiles.Find(browser, "Preferences", environment);
        RulePreview free = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        RulePreview locked;
        using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            locked = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Default", "Profile 1"], profiles.Select(p => p.Name));
        Assert.Equal(96, free.Bytes);
        Assert.Equal(PreviewState.Locked, locked.State);
        Assert.Equal(["Default", "Profile 1"], locked.LockedProfiles);
    }

    [Fact]
    public void A_running_browser_process_also_counts_as_a_lock()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        environment.Running.Add("alpha");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Cache\data_1", 64, Old);

        RulePreview preview = new CleanScanner(environment).Preview(CacheRule(Chromium()), TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Locked, preview.State);
    }

    [Fact]
    public void Gecko_caches_live_in_the_local_mirror_of_the_roaming_profile()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        var browser = new BrowserDefinition
        {
            Id = "beta",
            Name = "Beta",
            Engine = BrowserEngine.Gecko,
            UserData = [new LocationDefinition { Root = CleanRoot.RoamingAppData, Path = @"Beta\Profiles" }],
            CacheData = [new LocationDefinition { Root = CleanRoot.LocalAppData, Path = @"Beta\Profiles" }],
        };
        scratch.File(@"RoamingAppData\Beta\Profiles\abc.default\prefs.js");
        scratch.File(@"LocalAppData\Beta\Profiles\abc.default\cache2\entries\X", 40, Old);
        string parentLock = scratch.File(@"RoamingAppData\Beta\Profiles\abc.default\parent.lock");
        CleanRule rule = CacheRule(browser, "prefs.js", "cache2");

        RulePreview free = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        ProfileLockState state;
        using (new FileStream(parentLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            state = BrowserProfiles.LockState(browser, free.Profiles[0], environment.Running);
        }

        Assert.Equal(40, free.Bytes);
        Assert.Equal(Path.Combine(environment.Folder(CleanRoot.LocalAppData), @"Beta\Profiles\abc.default"), free.Profiles[0].CacheFolder);
        Assert.Equal(ProfileLockState.LockedByFile, state);
    }

    [Theory]
    [InlineData("thumbcache_96.db", true)]
    [InlineData("THUMBCACHE_IDX.DB", true)]
    [InlineData("thumbcache.txt", false)]
    [InlineData("iconcache_16.db", false)]
    public void Name_patterns_ignore_case(string name, bool expected) =>
        Assert.Equal(expected, CleanScanner.Matches(name, ["thumbcache_*.db"], ["iconcache*"]));

    private static BrowserDefinition Chromium() => new()
    {
        Id = "alpha",
        Name = "Alpha",
        Engine = BrowserEngine.Chromium,
        Processes = ["alpha"],
        UserData = [new LocationDefinition { Root = CleanRoot.LocalAppData, Path = @"Alpha\User Data" }],
    };

    private static CleanRule CacheRule(BrowserDefinition browser, string marker = "Preferences", string folder = "Cache") => new()
    {
        Id = $"browser.{browser.Id}.cache",
        Category = CleanCategory.BrowserCache,
        Name = new LocalizedText { En = "Cache", Id = "Cache" },
        Browser = browser,
        ProfileMarker = marker,
        Area = ProfileArea.Cache,
        Targets = [new TargetDefinition { Path = folder, Recurse = true }],
    };
}
