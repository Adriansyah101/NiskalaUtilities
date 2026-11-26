namespace Niskala.Scanner.Leftovers;

public sealed record OtherApp(string Name, AppTokens Tokens);

public sealed record LeftoverCandidate(
    LeftoverKind Kind,
    string Path,
    string? ValueName,
    string? Name,
    DateTime? Created,
    IReadOnlyList<(LeftoverSignal Signal, string Detail)> Evidence)
{
    public string? ReferencedFile { get; init; }

    public int? Files { get; init; }

    public long? Bytes { get; init; }

    public string? PersonalData { get; init; }
}

public sealed class ConfidenceScorer(DateOnly? installDate, IReadOnlyList<OtherApp> otherApps)
{
    public const int SafeThreshold = 80;
    public const int ReviewThreshold = 50;

    public static int Weight(LeftoverSignal signal) => signal switch
    {
        LeftoverSignal.InstallerIdentity => 70,
        LeftoverSignal.InFingerprint => 60,
        LeftoverSignal.PointsToFingerprint => 50,
        LeftoverSignal.ExactName => 40,
        LeftoverSignal.PublisherAndProduct => 25,
        LeftoverSignal.NearInstallDate => 10,
        LeftoverSignal.PartialName => -40,
        LeftoverSignal.OtherInstalledApp => -50,
        LeftoverSignal.SteamAppId => 90,
        LeftoverSignal.MissingProgram => 15,
        LeftoverSignal.PackageFamily => 90,
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };

    public static ConfidenceTier TierOf(int score) => score switch
    {
        >= SafeThreshold => ConfidenceTier.Safe,
        >= ReviewThreshold => ConfidenceTier.Review,
        _ => ConfidenceTier.Low,
    };

    public LeftoverItem Score(LeftoverCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        bool owned = candidate.Evidence.Any(e => e.Signal is LeftoverSignal.InstallerIdentity or LeftoverSignal.InFingerprint or LeftoverSignal.PointsToFingerprint);
        var reasons = new List<LeftoverReason>();
        foreach ((LeftoverSignal signal, string detail) in candidate.Evidence)
        {
            if (!(owned && signal == LeftoverSignal.PartialName))
            {
                Add(signal, detail);
            }
        }

        if (installDate is { } date && candidate.Created is { } created
            && Math.Abs(DateOnly.FromDateTime(created.ToLocalTime()).DayNumber - date.DayNumber) <= 1)
        {
            Add(LeftoverSignal.NearInstallDate, $"{(candidate.Kind is LeftoverKind.RegistryKey ? "written" : "created")} {created.ToLocalTime():yyyy-MM-dd}, installed {date:yyyy-MM-dd}");
        }

        if (candidate.Name is { } name && OtherMatch(name) is { } other)
        {
            Add(LeftoverSignal.OtherInstalledApp, $"\"{name}\" also matches {other}");
        }

        int sum = reasons.Sum(r => r.Weight);
        if (candidate.PersonalData is { } personal && sum >= Weight(LeftoverSignal.ExactName))
        {
            int review = Math.Clamp(sum, ReviewThreshold, SafeThreshold - 1);
            if (review != sum)
            {
                reasons.Add(new LeftoverReason(LeftoverSignal.PersonalData, review - sum, personal));
                sum = review;
            }
        }

        int score = Math.Clamp(sum, 0, 100);
        return new LeftoverItem(candidate.Kind, candidate.Path, candidate.ValueName, score, TierOf(score), reasons)
        {
            Files = candidate.Files,
            Bytes = candidate.Bytes,
        };

        void Add(LeftoverSignal signal, string detail)
        {
            if (reasons.TrueForAll(r => r.Signal != signal))
            {
                reasons.Add(new LeftoverReason(signal, Weight(signal), detail));
            }
        }
    }

    private string? OtherMatch(string name)
    {
        string compact = TokenNormalizer.Compact(TokenNormalizer.Normalize(name));
        if (compact.Length == 0)
        {
            return null;
        }

        foreach (OtherApp other in otherApps)
        {
            if (other.Tokens.Publisher == compact || other.Tokens.Product.Any(t => t.Compact == compact))
            {
                return other.Name;
            }
        }

        return null;
    }
}
