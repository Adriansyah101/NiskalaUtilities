using Niskala.Core.Models;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Tests;

public sealed class InventorySourcesTests
{
    private const AppSources Everything = AppSources.Registry | AppSources.Msi | AppSources.Appx;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_three_sources_are_read_at_the_same_time()
    {
        using var all = new CountdownEvent(3);
        IReadOnlyList<InstalledApp> Registry(CancellationToken token) => Meet(all, [TestEntries.App("reg")]);
        IReadOnlyList<MsiProduct> Msi(CancellationToken token) => Meet(all, [Product("Msi")]);
        IReadOnlyList<InstalledApp> Packages(CancellationToken token) => Meet(all, [TestEntries.App("pkg")]);

        InventorySources read = await InventoryCollector.ReadSourcesAsync(Everything, Registry, Msi, Packages, TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("reg", Assert.Single(read.Registry).Id);
        Assert.Equal("Msi", Assert.Single(read.Msi).Name);
        Assert.Equal("pkg", Assert.Single(read.Packages).Id);
    }

    [Fact]
    public async Task Each_source_keeps_its_own_slot_whatever_finishes_first()
    {
        IReadOnlyList<InstalledApp> Slow(CancellationToken token)
        {
            Thread.Sleep(150);
            return [TestEntries.App("slow-registry")];
        }

        InventorySources read = await InventoryCollector.ReadSourcesAsync(
            Everything,
            Slow,
            _ => [Product("fast")],
            _ => [TestEntries.App("fast-package")],
            TestContext.Current.CancellationToken);

        Assert.Equal("slow-registry", Assert.Single(read.Registry).Id);
        Assert.Equal("fast-package", Assert.Single(read.Packages).Id);
    }

    [Fact]
    public async Task Sources_that_are_off_are_never_read()
    {
        int calls = 0;
        InventorySources read = await InventoryCollector.ReadSourcesAsync(
            AppSources.Msi,
            _ => Count(ref calls, [TestEntries.App("reg")]),
            _ => [Product("only")],
            _ => Count(ref calls, [TestEntries.App("pkg")]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Empty(read.Registry);
        Assert.Empty(read.Packages);
        Assert.Single(read.Msi);
    }

    [Fact]
    public async Task A_cancelled_read_stops_with_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        IReadOnlyList<InstalledApp> Waits(CancellationToken token)
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return [];
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InventoryCollector.ReadSourcesAsync(Everything, Waits, _ => [], _ => [], cancel.Token));
    }

    private static IReadOnlyList<T> Meet<T>(CountdownEvent all, IReadOnlyList<T> result)
    {
        all.Signal();
        Assert.True(all.Wait(Timeout));
        return result;
    }

    private static IReadOnlyList<InstalledApp> Count(ref int calls, IReadOnlyList<InstalledApp> result)
    {
        Interlocked.Increment(ref calls);
        return result;
    }

    private static MsiProduct Product(string name) =>
        new(Guid.NewGuid(), InstallScope.Machine, name, "1.0", "Acme", null, null);
}
