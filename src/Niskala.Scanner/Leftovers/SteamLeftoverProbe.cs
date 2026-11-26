using Niskala.Scanner.Steam;

namespace Niskala.Scanner.Leftovers;

public sealed class SteamLeftoverProbe(Func<SteamLocations?> locations)
{
    internal void Find(ScanContext context, CandidateSet candidates)
    {
        if (SteamLibrary.AppId(context.App) is not { } appId || locations() is not { } steam)
        {
            return;
        }

        foreach (SteamRecord record in SteamRecords.Find(appId, steam))
        {
            candidates.Add(record.Kind, record.Path, null, null, null, LeftoverSignal.SteamAppId, record.Detail);
            if (record.PersonalData)
            {
                candidates.MarkPersonal(record.Kind, record.Path, record.Detail);
            }
        }

        if (context.Fingerprint.Roots.Count > 0 && context.Fingerprint.Roots[^1] is { } root && Directory.Exists(root)
            && SteamLibrary.Libraries(steam.ClientFolder).Any(l => string.Equals(Path.GetDirectoryName(root), Path.Combine(l, "steamapps", "common"), StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(LeftoverKind.Folder, root, null, null, null, LeftoverSignal.SteamAppId, "the game's folder in a Steam library");
        }
    }
}
