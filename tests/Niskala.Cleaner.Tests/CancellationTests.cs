using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Cleaner.Registry;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Cleaner.Tests;

public sealed class CancellationTests : IDisposable
{
    private const int StopBudgetMs = 50;
    private const int TreeFiles = 4000;
    private const int ClassKeys = 4000;
    private const int HandleSlack = 64;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-3);

    private readonly ScratchFolder _scratch = new();
    private readonly string _key = $@"{TestRoots.Key}\{Guid.NewGuid():N}";

    [Fact]
    public void A_cancelled_token_stops_the_rule_scan_before_any_file_is_listed()
    {
        var environment = new FakeCleanEnvironment(_scratch);
        _scratch.File(@"UserTemp\a.tmp", 10, Old);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new CleanScanner(environment).Preview([TempRule("t.one"), TempRule("t.two")], null, cancel.Token));
    }

    [Fact]
    public async Task The_rule_scan_stops_within_the_budget_mid_tree()
    {
        var environment = new FakeCleanEnvironment(_scratch);
        Tree();
        var scanner = new CleanScanner(environment);
        CleanRule[] rules = [TempRule("t.a"), TempRule("t.b"), TempRule("t.c"), TempRule("t.d")];

        TimeSpan stop = await StopTime(token => scanner.Preview(rules, null, token));

        Assert.InRange(stop.TotalMilliseconds, 0, StopBudgetMs);
    }

    [Fact]
    public void Repeated_cancelled_rule_scans_leave_no_open_handles()
    {
        var environment = new FakeCleanEnvironment(_scratch);
        Tree();
        var scanner = new CleanScanner(environment);
        CleanRule[] rules = [TempRule("t.a"), TempRule("t.b")];
        CancelAfterStart(scanner, rules);

        int before = Handles();
        for (int i = 0; i < 25; i++)
        {
            CancelAfterStart(scanner, rules);
        }

        Assert.InRange(Handles() - before, int.MinValue, HandleSlack);
    }

    [Fact]
    public void A_cancelled_token_stops_the_integrity_scan_at_once()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Integrity().Scan(cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task The_integrity_scan_stops_within_the_budget_and_closes_its_keys()
    {
        Classes();
        RegistryIntegrityScanner scanner = Integrity();
        await StopTime(token => scanner.Scan(cancellationToken: token));

        int before = Handles();
        TimeSpan slowest = TimeSpan.Zero;
        for (int i = 0; i < 10; i++)
        {
            TimeSpan stop = await StopTime(token => scanner.Scan(cancellationToken: token));
            slowest = stop > slowest ? stop : slowest;
        }

        Assert.InRange(slowest.TotalMilliseconds, 0, StopBudgetMs);
        Assert.InRange(Handles() - before, int.MinValue, HandleSlack);
    }

    public void Dispose()
    {
        Hives.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        _scratch.Dispose();
    }

    private static CleanRule TempRule(string id) =>
        Fixtures.Rule(id, new TargetDefinition { Root = CleanRoot.UserTemp, Files = ["*.tmp"], Recurse = true });

    private static async Task<TimeSpan> StopTime<T>(Func<CancellationToken, T> work)
    {
        using var cancel = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task running = Task.Run(() =>
        {
            started.Set();
            try
            {
                work(cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        Assert.True(started.Wait(Timeout));
        Thread.Sleep(5);
        long asked = Stopwatch.GetTimestamp();
        cancel.Cancel();
        await running.WaitAsync(Timeout);
        return Stopwatch.GetElapsedTime(asked);
    }

    private static void CancelAfterStart(CleanScanner scanner, CleanRule[] rules)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(3));
        try
        {
            scanner.Preview(rules, null, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int Handles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process self = Process.GetCurrentProcess();
        return self.HandleCount;
    }

    private void Tree()
    {
        for (int i = 0; i < TreeFiles; i++)
        {
            _scratch.File($@"UserTemp\d{i % 40}\e{i % 7}\f{i}.tmp", 1, Old);
        }
    }

    private void Classes()
    {
        using RegistryKey classes = Hives.CurrentUser.CreateSubKey($@"{_key}\Classes");
        for (int i = 0; i < ClassKeys; i++)
        {
            using RegistryKey type = classes.CreateSubKey($@"Nsk.Type{i}\shell\open\command");
            type.SetValue(string.Empty, $"\"{Path.Combine(_scratch.Root, "missing", "app" + i + ".exe")}\" \"%1\"");
        }
    }

    private RegistryIntegrityScanner Integrity() => new(
        new IntegrityLocations([$@"HKCU\{_key}\Classes"], [$@"HKCU\{_key}\SharedDLLs"], $@"HKCU\{_key}\Services"),
        RegistryGuard.ForCurrentMachine(),
        new FakeFirewall(),
        SystemCommandEnvironment.Instance,
        NullLogger<RegistryIntegrityScanner>.Instance);
}
