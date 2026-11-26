using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Scanner.Leftovers;

internal sealed class ScanContext
{
    internal ScanContext(InstalledApp app, Fingerprint fingerprint, ICommandEnvironment environment)
    {
        App = app;
        Fingerprint = fingerprint;
        Environment = environment;
        Tokens = TokenNormalizer.For(app);
        Owner = new OwnershipMatcher(fingerprint.Roots, environment);
    }

    public InstalledApp App { get; }

    public Fingerprint Fingerprint { get; }

    public AppTokens Tokens { get; }

    internal ICommandEnvironment Environment { get; }

    internal OwnershipMatcher Owner { get; }
}

internal sealed class CandidateSet
{
    private readonly Dictionary<(LeftoverKind, string, string?), Entry> _entries = [];

    public IEnumerable<LeftoverCandidate> Candidates => _entries.Values.Select(e => e.ToCandidate());

    public void Add(LeftoverKind kind, string path, string? valueName, string? name, DateTime? created, LeftoverSignal signal, string detail, string? referencedFile = null)
    {
        var key = (kind, path.ToUpperInvariant(), valueName?.ToUpperInvariant());
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry(kind, path, valueName, name, created);
            _entries.Add(key, entry);
        }

        entry.Evidence.Add((signal, detail));
        entry.ReferencedFile ??= referencedFile;
    }

    public void MarkPersonal(LeftoverKind kind, string path, string detail)
    {
        if (_entries.TryGetValue((kind, path.ToUpperInvariant(), null), out Entry? entry))
        {
            entry.PersonalData ??= detail;
        }
    }

    public void Add(LeftoverKind kind, string path, string? name, DateTime? created, params ReadOnlySpan<(LeftoverSignal Signal, string Detail)> evidence)
    {
        foreach ((LeftoverSignal signal, string detail) in evidence)
        {
            Add(kind, path, null, name, created, signal, detail);
        }
    }

    private sealed class Entry(LeftoverKind kind, string path, string? valueName, string? name, DateTime? created)
    {
        public List<(LeftoverSignal Signal, string Detail)> Evidence { get; } = [];

        public string? ReferencedFile { get; set; }

        public string? PersonalData { get; set; }

        public LeftoverCandidate ToCandidate() => new(kind, path, valueName, name, created, Evidence) { ReferencedFile = ReferencedFile, PersonalData = PersonalData };
    }
}
