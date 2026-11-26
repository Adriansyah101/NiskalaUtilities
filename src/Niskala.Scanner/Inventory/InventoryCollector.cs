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

public sealed class InventoryCollector(
    RegistryUninstallSource registry,
    MsiProductSource msi,
    AppxSource packages,
    InstallLocationEnricher enricher,
    ILogger<InventoryCollector> logger)
{
    public InventorySnapshot Collect(InventoryOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new InventoryOptions();
        long start = Stopwatch.GetTimestamp();

        IReadOnlyList<InstalledApp> registryApps = options.Sources.HasFlag(AppSources.Registry) ? registry.ReadAll(cancellationToken) : [];
        IReadOnlyList<MsiProduct> msiProducts = options.Sources.HasFlag(AppSources.Msi) ? msi.ReadAll(cancellationToken) : [];
        IReadOnlyList<InstalledApp> packageApps = options.Sources.HasFlag(AppSources.Appx) ? packages.ReadAll(cancellationToken) : [];

        IReadOnlyList<InstalledApp> merged = InventoryDeduplicator.Apply(registryApps, msiProducts, packageApps);
        IReadOnlyList<InstalledApp> enriched = enricher.Enrich(merged, options.BuildMsiComponentIndex, cancellationToken);

        List<InstalledApp> sorted = [.. enriched
            .OrderBy(a => a.DisplayName ?? "￿" + a.Id, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Id, StringComparer.Ordinal)];

        var snapshot = new InventorySnapshot(sorted, Stopwatch.GetElapsedTime(start));
        ScannerLog.InventoryCollected(logger, sorted.Count, snapshot.VisibleCount, (long)snapshot.Elapsed.TotalMilliseconds);
        return snapshot;
    }
}
