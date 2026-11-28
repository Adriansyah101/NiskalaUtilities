using System.Text.RegularExpressions;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Tests;

public sealed partial class LifecycleTests
{
    private static readonly string Repository = FindRepository();

    [Fact]
    public void Attaching_twice_subscribes_once_and_detaching_twice_unsubscribes_once()
    {
        var source = new Source();
        int raised = 0;
        void Handler() => raised++;
        var attachment = new Attachment(() => source.Changed += Handler, () => source.Changed -= Handler);

        Assert.True(attachment.Attach());
        Assert.False(attachment.Attach());
        source.Raise();
        Assert.Equal(1, raised);
        Assert.Equal(1, source.Subscribers);

        Assert.True(attachment.Detach());
        Assert.False(attachment.Detach());
        source.Raise();
        Assert.Equal(1, raised);
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public void Many_load_and_unload_cycles_never_stack_handlers()
    {
        var source = new Source();
        var attachment = new Attachment(() => source.Changed += Nothing, () => source.Changed -= Nothing);

        for (int i = 0; i < 100; i++)
        {
            attachment.Attach();
            attachment.Attach();
            Assert.Equal(1, source.Subscribers);
            attachment.Detach();
        }

        Assert.Equal(0, source.Subscribers);
        Assert.False(attachment.IsAttached);
    }

    [Fact]
    public void Stop_tolerates_missing_cancelled_and_disposed_sources()
    {
        Assert.False(TaskViewModel.Stop(null));

        using var live = new CancellationTokenSource();
        Assert.True(TaskViewModel.Stop(live));
        Assert.False(TaskViewModel.Stop(live));

        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        Assert.False(TaskViewModel.Stop(disposed));
    }

    [Fact]
    public void Stop_survives_a_callback_that_throws()
    {
        using var cancel = new CancellationTokenSource();
        bool second = false;
        cancel.Token.Register(() => throw new InvalidOperationException("callback"));
        cancel.Token.Register(() => second = true);

        Assert.True(TaskViewModel.Stop(cancel));
        Assert.True(cancel.IsCancellationRequested);
        Assert.True(second);
    }

    [Fact]
    public async Task Stop_from_many_threads_at_once_cancels_exactly_once()
    {
        using var cancel = new CancellationTokenSource();
        int callbacks = 0;
        cancel.Token.Register(() => Interlocked.Increment(ref callbacks));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => TaskViewModel.Stop(cancel), TestContext.Current.CancellationToken)));

        Assert.Equal(1, callbacks);
    }

    [Theory]
    [InlineData(3, 3, true, true)]
    [InlineData(2, 3, true, false)]
    [InlineData(3, 3, false, false)]
    [InlineData(4, 3, true, false)]
    public void Only_progress_from_the_current_run_reaches_the_screen(int run, int current, bool running, bool shown) =>
        Assert.Equal(shown, DriveOptimizerViewModel.IsCurrent(run, current, running));

    [Fact]
    public void View_models_cancel_through_the_safe_stop()
    {
        foreach (string file in Sources(Path.Combine("src", "Niskala.UI", "ViewModels")))
        {
            if (Path.GetFileName(file) == "TaskViewModel.cs")
            {
                continue;
            }

            Assert.DoesNotMatch(DirectCancel(), File.ReadAllText(file));
        }
    }

    [Fact]
    public void Every_dispatcher_timer_is_stopped_when_its_owner_is_disposed()
    {
        int timers = 0;
        foreach (string file in Sources(Path.Combine("src", "Niskala.UI")))
        {
            string code = File.ReadAllText(file);
            foreach (Match match in TimerField().Matches(code))
            {
                timers++;
                string name = match.Groups["name"].Value;
                Assert.True(code.Contains($"{name}.Stop();", StringComparison.Ordinal), $"{Path.GetFileName(file)} never stops {name}");
            }
        }

        Assert.True(timers > 0);
    }

    [Fact]
    public void Pages_attach_their_handlers_through_attachments()
    {
        string[] pages = ["DriveOptimizerPage.xaml.cs", "RepairPage.xaml.cs", "UpdatesPage.xaml.cs", "AppsPage.xaml.cs"];
        foreach (string page in pages)
        {
            string file = Assert.Single(Sources(Path.Combine("src", "Niskala.UI")), f => Path.GetFileName(f) == page);
            Assert.Contains("new Attachment(", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static void Nothing()
    {
    }

    private static IEnumerable<string> Sources(string relative) =>
        Directory.EnumerateFiles(Path.Combine(Repository, relative), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Niskala.sln")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("Niskala.sln was not found above the test folder.");
    }

    [GeneratedRegex(@"\b_\w+\?\.Cancel\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex DirectCancel();

    [GeneratedRegex(@"DispatcherQueueTimer\s+(?<name>_\w+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex TimerField();

    private sealed class Source
    {
        private Action? _changed;

        public event Action Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public void Raise() => _changed?.Invoke();
    }
}
