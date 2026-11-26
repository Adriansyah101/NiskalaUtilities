using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using static Niskala.Guardian.Tests.Guards;

namespace Niskala.Guardian.Tests;

public sealed class ScheduledTaskTests
{
    [Fact]
    public async Task A_task_is_backed_up_removed_and_registered_again_on_restore()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Programs\Acme\acme.exe"))!;
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        var tasks = new FakeTaskEditor { Tasks = { [@"\Acme\Updater"] = Xml(Path.Combine(root, "update.exe")), [@"\Acme\Viewer"] = Xml(notepad) } };
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        LeftoverItem[] items = [Item(LeftoverKind.ScheduledTask, @"\Acme\Updater"), Item(LeftoverKind.ScheduledTask, @"\Acme\Viewer"), Item(LeftoverKind.ScheduledTask, @"\Acme\Gone")];

        CleanResult result = await Cleaner(store, Real(), tasks: tasks)
            .CleanAsync(App(), Fingerprint(root), items, new CleanOptions { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([RemovalStatus.Removed, RemovalStatus.Skipped, RemovalStatus.Skipped], result.Transaction.Items.Select(i => i.Status));
        Assert.Equal("the task now starts a program the app does not own", result.Transaction.Items[1].Detail);
        Assert.Equal("already gone", result.Transaction.Items[2].Detail);
        Assert.Equal([@"\Acme\Viewer"], tasks.Tasks.Keys);
        ScheduledTaskXml saved = Assert.Single(store.Load(result.Transaction.Id)!.ScheduledTasks);
        Assert.Equal(@"\Acme\Updater", saved.Path);
        Assert.Contains("update.exe", saved.Xml, StringComparison.Ordinal);

        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), tasks, NullLogger<TransactionRestorer>.Instance).Restore(result.Transaction.Id)!;

        Assert.Empty(report.Problems);
        Assert.Equal(1, report.ItemsRestored);
        Assert.Equal(saved.Xml, tasks.Tasks[@"\Acme\Updater"]);
        Assert.Equal(RemovalStatus.Restored, store.Load(result.Transaction.Id)!.Items[0].Status);
    }

    [Fact]
    public async Task A_task_that_holds_the_path_again_is_left_as_it_is()
    {
        using var scratch = new ScratchFolder();
        string root = Path.GetDirectoryName(scratch.File(@"Programs\Acme\acme.exe"))!;
        var tasks = new FakeTaskEditor { Tasks = { [@"\Acme\Updater"] = Xml(Path.Combine(root, "update.exe")) } };
        var store = new TransactionStore(Path.Combine(scratch.Root, "journal"));
        CleanResult result = await Cleaner(store, Real(), tasks: tasks)
            .CleanAsync(App(), Fingerprint(root), [Item(LeftoverKind.ScheduledTask, @"\Acme\Updater")], new CleanOptions { CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);
        tasks.Tasks[@"\Acme\Updater"] = Xml(@"C:\New\updater.exe");

        RestoreReport report = new TransactionRestorer(store, new FakeFirewall(), tasks, NullLogger<TransactionRestorer>.Instance).Restore(result.Transaction.Id)!;

        Assert.Contains("a task holds that path now", Assert.Single(report.Problems), StringComparison.Ordinal);
        Assert.Contains(@"C:\New\updater.exe", tasks.Tasks[@"\Acme\Updater"], StringComparison.Ordinal);
    }

    [Fact]
    public void Task_Scheduler_registers_reads_and_deletes_a_task_from_its_definition()
    {
        string path = $@"\NiskalaTest-{Guid.NewGuid():N}";
        string xml = Xml(Path.Combine(TestRoots.Folder, "never-run.exe"), WindowsIdentity.GetCurrent().User!.Value);
        try
        {
            Assert.Equal(0, TaskSchedulerNative.TryRegister(path, xml));
            Assert.Equal(TaskSchedulerNative.AlreadyExists, TaskSchedulerNative.TryRegister(path, xml));
            Assert.Equal(0, TaskSchedulerNative.TryRead(path, out string? stored));
            Assert.Contains("never-run.exe", stored, StringComparison.Ordinal);

            Assert.Equal(0, TaskSchedulerNative.TryDelete(path));
            Assert.Equal(TaskSchedulerNative.NotFound, TaskSchedulerNative.TryRead(path, out _));
            Assert.Equal(TaskSchedulerNative.NotFound, TaskSchedulerNative.TryDelete(path));
            Assert.Equal(TaskSchedulerNative.NotFound, TaskSchedulerNative.TryDelete($@"\NiskalaTest-{Guid.NewGuid():N}\Missing"));
        }
        finally
        {
            TaskSchedulerNative.TryDelete(path);
        }
    }

    private static Fingerprint Fingerprint(string root) => new() { AppId = App().Id, TakenAt = DateTimeOffset.UtcNow, Roots = [root] };

    private static string Xml(string command, string? user = null) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Niskala tests</Author></RegistrationInfo>
          {(user is null ? string.Empty : $"<Principals><Principal id=\"Author\"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>")}
          <Settings><Enabled>false</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{command}</Command></Exec></Actions>
        </Task>
        """;
}
