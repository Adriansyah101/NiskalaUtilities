using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Interop.Sqlite;
using Niskala.Scanner.Leftovers;

namespace Niskala.Cleaner.Tests;

public sealed class SystemCleanerTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-3);

    private static readonly CleanRunOptions Options = new() { SubjectId = "selective-clean", SubjectName = "Selective Clean" };

    [Fact]
    public async Task Old_files_are_deleted_and_journaled_while_open_or_changed_ones_stay()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string temp = environment.Folder(CleanRoot.UserTemp);
        string a = scratch.File(@"UserTemp\a.tmp", 100, Old);
        string b = scratch.File(@"UserTemp\sub\deeper\b.tmp", 50, Old);
        string held = scratch.File(@"UserTemp\held.tmp", 10, Old);
        string rewritten = scratch.File(@"UserTemp\rewritten.tmp", 10, Old);
        CleanRule rule = Fixtures.Rule("t.temp", new TargetDefinition { Root = CleanRoot.UserTemp, Recurse = true }) with { MinimumAge = TimeSpan.FromHours(1) };
        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        File.WriteAllText(rewritten, "now");
        TransactionStore store = Fixtures.Store(scratch);
        var points = new FakeRestorePoints();

        CleanRun run;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            run = await new SystemCleaner(Fixtures.Journal(store, Fixtures.Guard(), points), environment, NullLogger<SystemCleaner>.Instance)
                .CleanAsync([preview], Options, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(Directory.Exists(Path.Combine(temp, "sub")));
        Assert.True(Directory.Exists(temp));
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(rewritten));
        RuleOutcome outcome = Assert.Single(run.Rules);
        Assert.Equal((2, 150L, 1, 1, 2), (outcome.Removed, outcome.Bytes, outcome.InUse, outcome.Changed, outcome.Folders));
        TransactionRecord saved = store.Load(run.Transaction.Id)!;
        Assert.Equal(TransactionKind.SystemClean, saved.Kind);
        Assert.NotNull(saved.FinishedAt);
        Assert.Equal(7, saved.RestorePoint?.SequenceNumber);
        RemovalRecord record = Assert.Single(saved.Items);
        Assert.Equal((LeftoverKind.Folder, temp, 2, 150L), (record.Kind, record.Path, record.Files, record.Bytes));
        Assert.Single(points.Requests);
    }

    [Fact]
    public async Task Files_under_a_protected_tree_are_refused()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string guarded = scratch.File(@"UserTemp\guarded\keep.tmp", 10, Old);
        string loose = scratch.File(@"UserTemp\loose.tmp", 10, Old);
        CleanRule rule = Fixtures.Rule("t.temp", new TargetDefinition { Root = CleanRoot.UserTemp, Recurse = true });
        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        var cleaner = new SystemCleaner(
            Fixtures.Journal(Fixtures.Store(scratch), Fixtures.Guard(Path.GetDirectoryName(guarded)!)),
            environment,
            NullLogger<SystemCleaner>.Instance);

        CleanRun run = await cleaner.CleanAsync([preview], Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(guarded));
        Assert.False(File.Exists(loose));
        Assert.Equal((1, 1), (run.Rules[0].Removed, run.Rules[0].Refused));
    }

    [Fact]
    public async Task Recycled_traces_are_journaled_per_file_and_come_back_on_restore()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string history = scratch.File(@"LocalAppData\Traces\History", 64, Old);
        string journalFile = scratch.File(@"LocalAppData\Traces\History-journal", 8, Old);
        CleanRule rule = Fixtures.Rule("t.history", new TargetDefinition { Root = CleanRoot.LocalAppData, Path = "Traces", Files = ["History*"] })
            with { Removal = RemovalMode.RecycleBin };
        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        TransactionStore store = Fixtures.Store(scratch);

        CleanRun run = await new SystemCleaner(Fixtures.Journal(store, Fixtures.Guard()), environment, NullLogger<SystemCleaner>.Instance)
            .CleanAsync([preview], Options with { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(history));
        Assert.Equal(2, run.Rules[0].Recycled);
        Assert.All(run.Transaction.Items.Where(i => i.Kind == LeftoverKind.File), i => Assert.Contains(@"\$Recycle.Bin\", i.RecycledPath, StringComparison.OrdinalIgnoreCase));

        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), new FakeTaskEditor(), NullLogger<TransactionRestorer>.Instance).Restore(run.Transaction.Id)!;

        Assert.Empty(report.Problems);
        Assert.Equal(64, new FileInfo(history).Length);
        Assert.True(File.Exists(journalFile));
    }

    [Fact]
    public async Task Permanent_overrides_the_Recycle_Bin()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        string cookies = scratch.File(@"LocalAppData\Traces\Cookies", 64, Old);
        CleanRule rule = Fixtures.Rule("t.cookies", new TargetDefinition { Root = CleanRoot.LocalAppData, Path = "Traces" }) with { Removal = RemovalMode.RecycleBin };
        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);

        CleanRun run = await new SystemCleaner(Fixtures.Journal(Fixtures.Store(scratch), Fixtures.Guard()), environment, NullLogger<SystemCleaner>.Instance)
            .CleanAsync([preview], Options with { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(cookies));
        Assert.Equal((1, 0), (run.Rules[0].Removed, run.Rules[0].Recycled));
        Assert.Equal(RemovalMethod.Permanent, run.Transaction.Method);
    }

    [Fact]
    public async Task Browser_rules_stop_when_the_browser_opens_between_scan_and_clean()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        string cache = scratch.File(@"LocalAppData\Alpha\User Data\Default\Cache\data_0", 32, Old);
        CleanRule rule = BrowserRule(RuleAction.Delete);
        RulePreview preview = new CleanScanner(environment).Preview(rule, TestContext.Current.CancellationToken);
        environment.Running.Add("alpha");

        CleanRun run = await new SystemCleaner(Fixtures.Journal(Fixtures.Store(scratch), Fixtures.Guard()), environment, NullLogger<SystemCleaner>.Instance)
            .CleanAsync([preview], Options with { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(cache));
        Assert.Contains("is open", run.Rules[0].Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compaction_runs_on_free_profiles_and_is_journaled()
    {
        using var scratch = new ScratchFolder();
        var environment = new FakeCleanEnvironment(scratch);
        scratch.File(@"LocalAppData\Alpha\User Data\Default\Preferences");
        string history = scratch.File(@"LocalAppData\Alpha\User Data\Default\History", 0);
        Assert.Equal(SqliteNative.Ok, SqliteDatabase.TryOpen(history, TimeSpan.Zero, out SqliteDatabase? database));
        using (database)
        {
            database!.Execute("CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT)");
            database.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) INSERT INTO urls(url) SELECT hex(randomblob(512)) FROM n");
            database.Execute("DELETE FROM urls");
        }

        RulePreview preview = new CleanScanner(environment).Preview(BrowserRule(RuleAction.Compact), TestContext.Current.CancellationToken);
        CleanRun run = await new SystemCleaner(Fixtures.Journal(Fixtures.Store(scratch), Fixtures.Guard()), environment, NullLogger<SystemCleaner>.Instance)
            .CleanAsync([preview], Options with { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        RemovalRecord record = Assert.Single(run.Transaction.Items);
        Assert.Equal((RemovalStatus.Compacted, history), (record.Status, record.Path));
        Assert.True(record.Bytes > 0);
        Assert.True(File.Exists(history));
    }

    private static CleanRule BrowserRule(RuleAction action) => new()
    {
        Id = action == RuleAction.Compact ? "browser.alpha.compact" : "browser.alpha.cache",
        Category = action == RuleAction.Compact ? CleanCategory.DatabaseCompaction : CleanCategory.BrowserCache,
        Name = new LocalizedText { En = "x", Id = "x" },
        Action = action,
        Browser = new BrowserDefinition
        {
            Id = "alpha",
            Name = "Alpha",
            Engine = BrowserEngine.Chromium,
            Processes = ["alpha"],
            UserData = [new LocationDefinition { Root = CleanRoot.LocalAppData, Path = @"Alpha\User Data" }],
        },
        ProfileMarker = "Preferences",
        Area = ProfileArea.Cache,
        Targets = action == RuleAction.Compact ? [] : [new TargetDefinition { Path = "Cache", Recurse = true }],
        Databases = action == RuleAction.Compact ? ["History"] : [],
    };
}
