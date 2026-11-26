using Niskala.Core.Models;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Inventory;

public static class InventoryDeduplicator
{
    public static IReadOnlyList<InstalledApp> Apply(
        IReadOnlyList<InstalledApp> registry,
        IReadOnlyList<MsiProduct> msiProducts,
        IReadOnlyList<InstalledApp> packages)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(msiProducts);
        ArgumentNullException.ThrowIfNull(packages);

        List<InstalledApp> result = MarkDuplicates(registry, RegistryKey, RegistryRank);
        MergeMsi(result, msiProducts);
        result.AddRange(MarkDuplicates(packages, app => app.Package?.FamilyName, PackageRank));
        return result;
    }

    private static List<InstalledApp> MarkDuplicates<TRank>(IReadOnlyList<InstalledApp> apps, Func<InstalledApp, string?> key, Func<InstalledApp, TRank> rank)
        where TRank : IComparable<TRank>
    {
        List<InstalledApp> result = [.. apps];
        var primaryByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < result.Count; i++)
        {
            InstalledApp app = result[i];
            if (!app.IsVisible || key(app) is not { } duplicateKey)
            {
                continue;
            }

            if (!primaryByKey.TryGetValue(duplicateKey, out int primaryIndex))
            {
                primaryByKey.Add(duplicateKey, i);
                continue;
            }

            InstalledApp primary = result[primaryIndex];
            if (rank(app).CompareTo(rank(primary)) > 0)
            {
                result[primaryIndex] = primary with { HiddenReason = HiddenReason.Duplicate, DuplicateOfId = app.Id };
                primaryByKey[duplicateKey] = i;
            }
            else
            {
                result[i] = app with { HiddenReason = HiddenReason.Duplicate, DuplicateOfId = primary.Id };
            }
        }

        return result;
    }

    private static void MergeMsi(List<InstalledApp> apps, IReadOnlyList<MsiProduct> products)
    {
        Dictionary<Guid, MsiProduct> byCode = products
            .GroupBy(p => p.ProductCode)
            .ToDictionary(g => g.Key, g => g.First());
        var matched = new HashSet<Guid>();

        for (int i = 0; i < apps.Count; i++)
        {
            if (apps[i].MsiProductCode is { } code && byCode.TryGetValue(code, out MsiProduct? product))
            {
                apps[i] = Enrich(apps[i], product);
                matched.Add(code);
            }
        }

        apps.AddRange(products.Where(p => !matched.Contains(p.ProductCode)).DistinctBy(p => p.ProductCode).Select(FromProduct));
    }

    private static InstalledApp Enrich(InstalledApp app, MsiProduct product)
    {
        bool takeLocation = app.InstallLocation is null && product.InstallLocation is not null;
        bool takeDate = app.InstallDateOrigin != InstallDateOrigin.Registry && product.InstallDate is not null;
        return app with
        {
            Sources = app.Sources | AppSources.Msi,
            Publisher = app.Publisher ?? product.Publisher,
            DisplayVersion = app.DisplayVersion ?? product.Version,
            InstallLocation = takeLocation ? product.InstallLocation : app.InstallLocation,
            InstallLocationOrigin = takeLocation ? InstallLocationOrigin.MsiProperty : app.InstallLocationOrigin,
            InstallDate = takeDate ? product.InstallDate : app.InstallDate,
            InstallDateOrigin = takeDate ? InstallDateOrigin.MsiProperty : app.InstallDateOrigin,
        };
    }

    private static InstalledApp FromProduct(MsiProduct product) => new()
    {
        Id = $"msi:{product.ProductCode.ToString("B").ToUpperInvariant()}",
        Sources = AppSources.Msi,
        Scope = product.Scope,
        DisplayName = product.Name,
        DisplayVersion = product.Version,
        Publisher = product.Publisher,
        InstallLocation = product.InstallLocation,
        InstallLocationOrigin = product.InstallLocation is null ? InstallLocationOrigin.None : InstallLocationOrigin.MsiProperty,
        InstallDate = product.InstallDate,
        InstallDateOrigin = product.InstallDate is null ? InstallDateOrigin.None : InstallDateOrigin.MsiProperty,
        InstallerKind = InstallerKind.Msi,
        DetectionEvidence = ["registered with Windows Installer only"],
        MsiProductCode = product.ProductCode,
        HiddenReason = product.Name is null ? HiddenReason.NoDisplayName : HiddenReason.NoUninstallEntry,
    };

    private static string? RegistryKey(InstalledApp app)
    {
        if (app.MsiProductCode is { } code)
        {
            return $"msi|{app.Scope}|{code:B}";
        }

        return app.UninstallString is null
            ? null
            : $"cmd|{app.Scope}|{app.DisplayName}|{app.DisplayVersion}|{app.Publisher}|{app.UninstallString}";
    }

    private static int RegistryRank(InstalledApp app) =>
        (app.View == RegistryViewKind.Registry64 ? 4 : 0)
        + (app.InstallLocation is not null ? 2 : 0)
        + (app.DisplayIcon is not null ? 1 : 0);

    private static Version PackageRank(InstalledApp app) =>
        Version.TryParse(app.DisplayVersion, out Version? version) ? version : new Version(0, 0);
}
