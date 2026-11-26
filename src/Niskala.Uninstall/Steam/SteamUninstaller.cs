using Microsoft.Extensions.Logging;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Scanner.Steam;

namespace Niskala.Uninstall.Steam;

public enum SteamRemoval
{
    Direct,
    CloseSteam,
    ViaSteam,
}

public enum SteamOutcome
{
    Removed,
    SteamRunning,
    HandedToSteam,
    SteamNotStarted,
}

public sealed record SteamResult(SteamOutcome Outcome, CleanResult? Clean, int Error = 0);

public sealed record SteamPlan(InstalledApp App, int AppId, string ClientFolder, SteamGame? Game, Fingerprint Fingerprint, IReadOnlyList<LeftoverItem> Items, bool SteamRunning);

public sealed record SteamOptions
{
    public SteamRemoval Removal { get; init; }

    public bool Permanent { get; init; }

    public bool CreateRestorePoint { get; init; } = true;
}

public sealed class SteamUninstaller(
    FingerprintCollector fingerprints,
    FingerprintStore store,
    LeftoverCleaner cleaner,
    ISteamClient steam,
    ILogger<SteamUninstaller> logger,
    Func<string, SteamLocations>? locations = null)
{
    private const int FullScore = 100;
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(60);

    private readonly Func<string, SteamLocations> _locations = locations ?? (client => new SteamLocations(client));

    public static IReadOnlyList<LeftoverItem> Items(InstalledApp app, int appId, SteamGame? game, SteamLocations locations)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(locations);
        var items = new List<LeftoverItem>();
        if (game is not null && Directory.Exists(game.InstallFolder))
        {
            items.Add(Item(LeftoverKind.Folder, game.InstallFolder, $"install folder named by {Path.GetFileName(game.ManifestFile)}") with { Bytes = game.Manifest.SizeOnDisk });
        }

        if (game is not null && File.Exists(game.ManifestFile))
        {
            items.Add(Item(LeftoverKind.File, game.ManifestFile, "Steam's manifest of the game") with { Files = 1, Bytes = new FileInfo(game.ManifestFile).Length });
        }

        items.AddRange(SteamRecords.Find(appId, locations)
            .Where(r => !r.PersonalData)
            .Select(r => Item(r.Kind, r.Path, r.Detail)));
        if (app.RegistryKey is { } key && !items.Exists(i => i.Kind == LeftoverKind.RegistryKey && i.Path.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(Item(LeftoverKind.RegistryKey, key, "the game's own Uninstall entry"));
        }

        return items;
    }

    public SteamPlan? Plan(InstalledApp app, out string? problem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (SteamLibrary.AppId(app) is not { } appId)
        {
            problem = "not a Steam game";
            return null;
        }

        if (steam.ClientFolder() is not { } client)
        {
            problem = "the Steam client's folder is not in the registry";
            return null;
        }

        SteamGame? game = SteamLibrary.Find(appId, client);
        if (game is not null
            && app.InstallLocationGuard == PathVerdict.Allowed
            && PathSyntax.TryNormalize(app.InstallLocation, out string location) == PathVerdict.Allowed
            && PathSyntax.TryNormalize(game.InstallFolder, out string folder) == PathVerdict.Allowed
            && !location.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"the Uninstall entry names {location}, Steam's manifest names {folder}";
            return null;
        }

        problem = null;
        Fingerprint fingerprint = fingerprints.Collect(app, cancellationToken: cancellationToken);
        return new SteamPlan(app, appId, client, game, fingerprint, Items(app, appId, game, _locations(client)), steam.IsRunning(client));
    }

    public async Task<SteamResult> ExecuteAsync(SteamPlan plan, SteamOptions options, IProgress<CleanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        _ = store.Save(plan.App, plan.Fingerprint);
        if (options.Removal == SteamRemoval.ViaSteam)
        {
            if (plan.Game is null)
            {
                throw new InvalidOperationException("Steam cannot uninstall a game whose files are gone.");
            }

            int error = steam.OpenUninstall(plan.ClientFolder, plan.AppId);
            UninstallLog.SteamHandedOver(logger, plan.AppId, error);
            return new SteamResult(error == 0 ? SteamOutcome.HandedToSteam : SteamOutcome.SteamNotStarted, null, error);
        }

        if (steam.IsRunning(plan.ClientFolder))
        {
            if (options.Removal != SteamRemoval.CloseSteam)
            {
                return new SteamResult(SteamOutcome.SteamRunning, null);
            }

            int error = steam.Shutdown(plan.ClientFolder);
            if (error != 0)
            {
                return new SteamResult(SteamOutcome.SteamNotStarted, null, error);
            }

            bool closed = await steam.WaitForExitAsync(plan.ClientFolder, ShutdownWait, cancellationToken).ConfigureAwait(false);
            UninstallLog.SteamClosed(logger, closed);
            if (!closed)
            {
                return new SteamResult(SteamOutcome.SteamRunning, null);
            }
        }

        IReadOnlyList<LeftoverItem> items = Items(plan.App, plan.AppId, plan.Game, _locations(plan.ClientFolder));
        var cleanOptions = new CleanOptions { Kind = TransactionKind.Force, Permanent = options.Permanent, CreateRestorePoint = options.CreateRestorePoint };
        CleanResult clean = await cleaner.CleanAsync(plan.App, plan.Fingerprint, items, cleanOptions, progress, cancellationToken).ConfigureAwait(false);
        return new SteamResult(SteamOutcome.Removed, clean);
    }

    private static LeftoverItem Item(LeftoverKind kind, string path, string detail) =>
        new(kind, path, null, FullScore, ConfidenceTier.Safe, [new LeftoverReason(LeftoverSignal.InstallerIdentity, FullScore, detail)]);
}
