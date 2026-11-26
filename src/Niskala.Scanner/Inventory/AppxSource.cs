using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using PackageSignatureKind = Niskala.Core.Models.PackageSignatureKind;

namespace Niskala.Scanner.Inventory;

public sealed class AppxSource(ILogger<AppxSource> logger)
{
    public IReadOnlyList<InstalledApp> ReadAll(CancellationToken cancellationToken = default)
    {
        IEnumerable<Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            ScannerLog.PackageQueryFailed(logger, ex);
            return [];
        }

        var apps = new List<InstalledApp>();
        foreach (Package package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                apps.Add(Map(package));
            }
            catch (COMException ex)
            {
                ScannerLog.PackageReadFailed(logger, package.Id.FullName, ex);
            }
        }

        ScannerLog.PackagesRead(logger, apps.Count);
        return apps;
    }

    private InstalledApp Map(Package package)
    {
        PackageId id = package.Id;
        string? location = Try(() => package.InstalledPath);
        PackageSignatureKind signature = (PackageSignatureKind)(int)package.SignatureKind;
        DateOnly? installed = location is not null && Directory.Exists(location)
            ? DateOnly.FromDateTime(Directory.GetCreationTime(location))
            : null;
        PackageManifest manifest = location is null ? PackageManifest.Empty : PackageManifest.Read(location);

        return new InstalledApp
        {
            Id = $"appx:{id.FamilyName}",
            Sources = AppSources.Appx,
            Scope = InstallScope.User,
            DisplayName = Resolved(Try(() => package.DisplayName)) ?? id.Name,
            DisplayVersion = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
            Publisher = Resolved(Try(() => package.PublisherDisplayName)) ?? id.Publisher,
            InstallLocation = location,
            InstallLocationOrigin = location is null ? InstallLocationOrigin.None : InstallLocationOrigin.Package,
            InstallDate = installed,
            InstallDateOrigin = installed is null ? InstallDateOrigin.None : InstallDateOrigin.PackageFolder,
            InstallerKind = InstallerKind.Msix,
            Package = new PackageDetails(
                id.FullName,
                id.FamilyName,
                signature,
                manifest.AppListLogo ?? Try(() => package.Logo)?.LocalPath,
                manifest.LogoBackground,
                package.IsDevelopmentMode),
            IsProtected = signature == PackageSignatureKind.System,
            HiddenReason = signature == PackageSignatureKind.System ? HiddenReason.SystemComponent
                : manifest.AllowsExternalContent ? HiddenReason.SparsePackage
                : HiddenReason.None,
        };
    }

    private static string? Resolved(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : text;

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }
}
