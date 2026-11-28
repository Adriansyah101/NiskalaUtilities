using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Inventory;

public sealed record InventoryOptions
{
    public AppSources Sources { get; init; } = AppSources.Registry | AppSources.Msi | AppSources.Appx;

    public bool BuildMsiComponentIndex { get; init; } = true;
}

public sealed record InventorySnapshot(IReadOnlyList<InstalledApp> Apps, TimeSpan Elapsed)
{
    public int VisibleCount => Apps.Count(a => a.IsVisible);
}

public sealed record InventorySources(IReadOnlyList<InstalledApp> Registry, IReadOnlyList<MsiProduct> Msi, IReadOnlyList<InstalledApp> Packages);

public sealed class InventoryCollector(
    RegistryUninstallSource registry,
    MsiProductSource msi,
    AppxSource packages,
    InstallLocationEnricher enricher,
    ILogger<InventoryCollector> logger)
{
    public InventorySnapshot Collect(InventoryOptions? options = null, CancellationToken cancellationToken = default) =>
        CollectAsync(options, cancellationToken).GetAwaiter().GetResult();

    public async Task<InventorySnapshot> CollectAsync(InventoryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new InventoryOptions();
        long start = Stopwatch.GetTimestamp();
        InventorySources read = await ReadSourcesAsync(options.Sources, registry.ReadAll, msi.ReadAll, packages.ReadAll, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InstalledApp> merged = InventoryDeduplicator.Apply(read.Registry, read.Msi, read.Packages);
        IReadOnlyList<InstalledApp> enriched = enricher.Enrich(merged, options.BuildMsiComponentIndex, cancellationToken);

        List<InstalledApp> sorted = [.. enriched
            .OrderBy(a => a.DisplayName ?? "￿" + a.Id, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.Ordinal)];

        var snapshot = new InventorySnapshot(sorted, Stopwatch.GetElapsedTime(start));
        ScannerLog.InventoryCollected(logger, sorted.Count, snapshot.VisibleCount, (long)snapshot.Elapsed.TotalMilliseconds);
        return snapshot;
    }

    public static async Task<InventorySources> ReadSourcesAsync(
        AppSources sources,
        Func<CancellationToken, IReadOnlyList<InstalledApp>> registry,
        Func<CancellationToken, IReadOnlyList<MsiProduct>> msi,
        Func<CancellationToken, IReadOnlyList<InstalledApp>> packages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(msi);
        ArgumentNullException.ThrowIfNull(packages);
        Task<IReadOnlyList<InstalledApp>> fromRegistry = Start(sources.HasFlag(AppSources.Registry), registry, cancellationToken);
        Task<IReadOnlyList<MsiProduct>> fromMsi = Start(sources.HasFlag(AppSources.Msi), msi, cancellationToken);
        Task<IReadOnlyList<InstalledApp>> fromPackages = Start(sources.HasFlag(AppSources.Appx), packages, cancellationToken);
        await Task.WhenAll(fromRegistry, fromMsi, fromPackages).ConfigureAwait(false);
        return new InventorySources(await fromRegistry.ConfigureAwait(false), await fromMsi.ConfigureAwait(false), await fromPackages.ConfigureAwait(false));
    }

    private static Task<IReadOnlyList<T>> Start<T>(bool wanted, Func<CancellationToken, IReadOnlyList<T>> read, CancellationToken cancellationToken) =>
        wanted ? Task.Run(() => read(cancellationToken), cancellationToken) : Task.FromResult<IReadOnlyList<T>>([]);
}
