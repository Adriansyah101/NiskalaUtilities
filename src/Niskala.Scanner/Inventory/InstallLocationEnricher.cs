using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Inventory;

public sealed class InstallLocationEnricher(
    PathGuard guard,
    Func<IReadOnlyCollection<Guid>, IMsiComponentPaths> createComponentIndex,
    MsiLocationCache? cache,
    ILogger<InstallLocationEnricher> logger)
{
    private static readonly string[] SharedUninstallerFolders =
        ["Package Cache", "InstallShield Installation Information", "Installer", "Temp"];

    public IReadOnlyList<InstalledApp> Enrich(IReadOnlyList<InstalledApp> apps, bool buildComponentIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);

        List<Guid> misses = [.. apps
            .Where(NeedsInference)
            .Where(app => cache is null || !cache.TryGet(app.MsiProductCode!.Value, app.KeyLastWriteUtc!.Value, out _))
            .Select(app => app.MsiProductCode!.Value)];
        IMsiComponentPaths? index = buildComponentIndex && misses.Count > 0 ? createComponentIndex(misses) : null;

        var result = new List<InstalledApp>(apps.Count);
        foreach (InstalledApp app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Enrich(app, index));
        }

        cache?.Save();
        return result;
    }

    private InstalledApp Enrich(InstalledApp app, IMsiComponentPaths? index)
    {
        if (app.InstallLocation is { } recorded)
        {
            return app with { InstallLocationGuard = guard.Check(recorded).Verdict };
        }

        if (NeedsInference(app))
        {
            Guid code = app.MsiProductCode!.Value;
            DateTime stamp = app.KeyLastWriteUtc!.Value;
            if (cache is not null && cache.TryGet(code, stamp, out string? cached))
            {
                return With(app, Accept(cached), InstallLocationOrigin.MsiComponents);
            }

            if (index is null)
            {
                return app;
            }

            IReadOnlyList<string> paths = index.GetComponentPaths(code);
            string? inferred = Accept(MsiLocationInference.Infer(paths, guard));
            cache?.Set(code, stamp, inferred);
            ScannerLog.LocationInferred(logger, app.Id, inferred, paths.Count);
            return With(app, inferred, InstallLocationOrigin.MsiComponents);
        }

        if (app.InstallerKind is not (InstallerKind.Msi or InstallerKind.Msix or InstallerKind.Steam)
            && app.UninstallCommand is { ExecutableExists: true, Executable: { } executable }
            && Path.GetDirectoryName(executable) is { } directory
            && !IsSharedUninstallerFolder(directory))
        {
            return With(app, Accept(directory), InstallLocationOrigin.UninstallerDirectory);
        }

        return app;
    }

    private static bool NeedsInference(InstalledApp app) =>
        app.InstallLocation is null
        && app.InstallerKind == InstallerKind.Msi
        && app.MsiProductCode is not null
        && app.KeyLastWriteUtc is not null;

    private string? Accept(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        PathDecision decision = guard.Check(candidate.Trim().TrimEnd('\\'));
        return decision is { IsAllowed: true, Exists: true } ? decision.CanonicalPath : null;
    }

    private static InstalledApp With(InstalledApp app, string? location, InstallLocationOrigin origin) => location is null
        ? app
        : app with { InstallLocation = location, InstallLocationOrigin = origin, InstallLocationGuard = PathVerdict.Allowed };

    private static bool IsSharedUninstallerFolder(string directory) =>
        PathSyntax.TryNormalize(directory, out string normalized) != PathVerdict.Allowed
        || PathSyntax.GetSegments(normalized).Any(segment => SharedUninstallerFolders.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
