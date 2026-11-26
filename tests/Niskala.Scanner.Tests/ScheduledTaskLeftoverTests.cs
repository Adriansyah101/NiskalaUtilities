using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Firewall;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class ScheduledTaskLeftoverTests
{
    [Fact]
    public void Tasks_that_start_the_app_or_are_named_like_it_with_their_program_gone_are_found()
    {
        using var scratch = new ScratchFolder();
        string root = Path.Combine(scratch.Root, @"Programs\Acme Photo Editor");
        string other = scratch.File(@"Programs\Other\other.exe", 10);
        InstalledApp app = App("reg:hklm:AcmePhoto", "Photo Editor") with { Publisher = "Acme" };
        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = DateTimeOffset.UtcNow,
            Roots = [root],
            ScheduledTasks = [new TaskTrace(@"\Acme\Photo Editor Update", Path.Combine(root, "update.exe"), "/silent")],
        };
        var system = new FakeTraceSource
        {
            TaskList = new TaskList(
            [
                new TaskTrace(@"\Acme\Photo Editor Update", Path.Combine(root, "update.exe"), "/silent"),
                new TaskTrace(@"\Backup", @"%SystemRoot%\System32\cmd.exe", $"/c \"{root}\\backup.cmd\""),
                new TaskTrace(@"\Photo Editor", Path.Combine(scratch.Root, @"Gone\editor.exe"), null),
                new TaskTrace(@"\Acme\Photo Editor", Path.Combine(scratch.Root, @"Gone\editor.exe"), null),
                new TaskTrace(@"\Photo Editor Sync", other, null),
                new TaskTrace(@"\Microsoft\Windows\Photo Editor", Path.Combine(root, "editor.exe"), null),
            ], 0, 0),
        };

        LeftoverReport report = Scanner(system).Scan(app, fingerprint, [], TestContext.Current.CancellationToken);

        LeftoverItem update = Task(report, @"\Acme\Photo Editor Update");
        Assert.Equal(ConfidenceTier.Safe, update.Tier);
        Assert.Contains(update.Reasons, r => r.Signal == LeftoverSignal.InFingerprint);
        Assert.Contains(update.Reasons, r => r.Signal == LeftoverSignal.PointsToFingerprint);
        Assert.Contains(Task(report, @"\Backup").Reasons, r => r.Signal == LeftoverSignal.PointsToFingerprint && r.Detail.Contains("backup.cmd", StringComparison.Ordinal));
        LeftoverItem orphan = Task(report, @"\Photo Editor");
        Assert.Equal(ConfidenceTier.Review, orphan.Tier);
        Assert.Equal([LeftoverSignal.ExactName, LeftoverSignal.MissingProgram], orphan.Reasons.Select(r => r.Signal));
        Assert.Equal(ConfidenceTier.Safe, Task(report, @"\Acme\Photo Editor").Tier);
        Assert.DoesNotContain(report.Items, i => i.Path is @"\Photo Editor Sync" or @"\Microsoft\Windows\Photo Editor");
    }

    [Fact]
    public void A_task_read_again_must_still_start_only_the_app_s_programs_or_missing_ones()
    {
        var owner = new OwnershipMatcher([@"C:\Apps\Acme"], SystemCommandEnvironment.Instance);
        string mine = Xml(@"C:\Apps\Acme\update.exe");
        string gone = Xml(@"C:\Gone\nothing-here\update.exe");
        string notepad = Xml(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));

        Assert.True(ScheduledTaskLeftoverProbe.StillLeftover(@"\Acme", mine, owner, SystemCommandEnvironment.Instance));
        Assert.True(ScheduledTaskLeftoverProbe.StillLeftover(@"\Acme", gone, owner, SystemCommandEnvironment.Instance));
        Assert.False(ScheduledTaskLeftoverProbe.StillLeftover(@"\Acme", notepad, owner, SystemCommandEnvironment.Instance));
    }

    internal static string Xml(string command) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings><Enabled>false</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{command}</Command></Exec></Actions>
        </Task>
        """;

    private static LeftoverItem Task(LeftoverReport report, string path) =>
        Assert.Single(report.Items, i => i.Kind == LeftoverKind.ScheduledTask && i.Path == path);

    private static LeftoverScanner Scanner(FakeTraceSource system) => new(
        ZGuard(),
        RegistryGuard.ForCurrentMachine(),
        new NoTrust(),
        new FileLeftoverProbe([], system),
        new RegistryLeftoverProbe(system, NullLogger<RegistryLeftoverProbe>.Instance),
        new FirewallLeftoverProbe(new NoRules()),
        new SteamLeftoverProbe(() => null),
        new ScheduledTaskLeftoverProbe(system),
        new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
        SystemCommandEnvironment.Instance,
        NullLogger<LeftoverScanner>.Instance);

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
