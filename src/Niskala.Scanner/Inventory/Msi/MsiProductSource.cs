using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Interop.Msi;

namespace Niskala.Scanner.Inventory.Msi;

public sealed record MsiProduct(
    Guid ProductCode,
    InstallScope Scope,
    string? Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    DateOnly? InstallDate);

public sealed class MsiProductSource(ILogger<MsiProductSource> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IReadOnlyList<MsiProduct> ReadAll(CancellationToken cancellationToken = default)
    {
        int rc = MsiNative.TryEnumerateProducts(out List<MsiProductRef> references);
        if (rc != MsiNative.ErrorSuccess)
        {
            ScannerLog.MsiCallFailed(logger, "MsiEnumProductsEx", "*", rc);
        }

        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var products = new List<MsiProduct>(references.Count);
        foreach (MsiProductRef reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(reference.ProductCode, "B", out Guid code))
            {
                continue;
            }

            products.Add(new MsiProduct(
                code,
                reference.Context == MsiContext.Machine ? InstallScope.Machine : InstallScope.User,
                Read(reference, "ProductName"),
                Read(reference, "VersionString"),
                Read(reference, "Publisher"),
                Read(reference, "InstallLocation")?.TrimEnd('\\'),
                InstallDates.Parse(Read(reference, "InstallDate"), today)));
        }

        ScannerLog.MsiProductsRead(logger, products.Count);
        return products;
    }

    private string? Read(MsiProductRef reference, string property)
    {
        int rc = MsiNative.TryGetProductInfo(reference.ProductCode, reference.Context, property, out string? value);
        if (rc is not (MsiNative.ErrorSuccess or MsiNative.ErrorUnknownProperty or MsiNative.ErrorUnknownProduct))
        {
            ScannerLog.MsiCallFailed(logger, "MsiGetProductInfoEx", reference.ProductCode, rc);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
