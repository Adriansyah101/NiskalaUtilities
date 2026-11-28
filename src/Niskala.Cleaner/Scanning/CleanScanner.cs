using System.IO.Enumeration;
using Niskala.Cleaner.Browsers;
using Niskala.Cleaner.Rules;
using Niskala.Core.Safety;

namespace Niskala.Cleaner.Scanning;

public sealed record CleanFile(string Path, long Bytes);

public sealed record CleanFolder(string Path, bool Recurse, bool KeepFolders);

public enum PreviewState
{
    Ready,
    Empty,
    NeedsElevation,
    Locked,
}

public sealed record RulePreview(CleanRule Rule)
{
    public IReadOnlyList<CleanFile> Files { get; init; } = [];

    public IReadOnlyList<CleanFolder> Folders { get; init; } = [];

    public IReadOnlyList<string> Databases { get; init; } = [];

    public IReadOnlyList<BrowserProfile> Profiles { get; init; } = [];

    public IReadOnlyList<string> LockedProfiles { get; init; } = [];

    public bool NeedsElevation { get; init; }

    public long Bytes { get; init; }

    public int Count => Rule.Action == RuleAction.Compact ? Databases.Count : Files.Count;

    public PreviewState State => NeedsElevation ? PreviewState.NeedsElevation
        : Count > 0 ? PreviewState.Ready
        : LockedProfiles.Count > 0 ? PreviewState.Locked
        : PreviewState.Empty;
}

public sealed class CleanScanner(ICleanEnvironment environment)
{
    private const int MaxParallelRules = 4;

    public IReadOnlyList<RulePreview> Preview(IReadOnlyList<CleanRule> rules, IProgress<RulePreview>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        IReadOnlySet<string> running = environment.RunningProcessNames();
        var previews = new RulePreview[rules.Count];
        Parallel.For(0, rules.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRules, CancellationToken = cancellationToken }, i =>
        {
            previews[i] = Preview(rules[i], running, cancellationToken);
            progress?.Report(previews[i]);
        });
        return previews;
    }

    public RulePreview Preview(CleanRule rule, CancellationToken cancellationToken = default) =>
        Preview(rule, environment.RunningProcessNames(), cancellationToken);

    internal static bool IsOldEnough(DateTimeOffset now, TimeSpan minimumAge, DateTime lastWriteUtc, DateTime creationUtc) =>
        minimumAge <= TimeSpan.Zero || now.UtcDateTime - (lastWriteUtc > creationUtc ? lastWriteUtc : creationUtc) >= minimumAge;

    internal static bool Matches(string name, IReadOnlyList<string> include, IReadOnlyList<string> exclude) =>
        include.Any(p => FileSystemName.MatchesSimpleExpression(p, name)) && !exclude.Any(p => FileSystemName.MatchesSimpleExpression(p, name));

    private RulePreview Preview(CleanRule rule, IReadOnlySet<string> running, CancellationToken cancellationToken)
    {
        if (rule.RequiresElevation && !environment.IsElevated)
        {
            return new RulePreview(rule) { NeedsElevation = true };
        }

        var folders = new List<(string Folder, TargetDefinition Target)>();
        var databases = new List<string>();
        var locked = new List<string>();
        var free = new List<BrowserProfile>();
        if (rule.Browser is { } browser)
        {
            foreach (BrowserProfile profile in BrowserProfiles.Find(browser, rule.ProfileMarker!, environment))
            {
                if (BrowserProfiles.LockState(browser, profile, running) != ProfileLockState.Free)
                {
                    locked.Add(profile.Name);
                    continue;
                }

                free.Add(profile);
                string baseFolder = rule.Area == ProfileArea.Cache ? profile.CacheFolder : profile.ProfileFolder;
                folders.AddRange(rule.Targets.Select(t => (Path.Combine(baseFolder, t.Path), t)));
                databases.AddRange(rule.Databases.Select(d => Path.Combine(profile.ProfileFolder, d)).Where(File.Exists));
            }
        }
        else
        {
            foreach (TargetDefinition target in rule.Targets)
            {
                if (environment.Resolve(target.Root!.Value) is { } root)
                {
                    folders.Add((Path.Combine(root, target.Path), target));
                }
            }
        }

        if (rule.Action == RuleAction.Compact)
        {
            return new RulePreview(rule) { Databases = databases, Profiles = free, LockedProfiles = locked, Bytes = databases.Sum(FileFacts.Size) };
        }

        var files = new List<CleanFile>();
        var scanned = new List<CleanFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string folder, TargetDefinition target) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder) || FileFacts.IsLink(folder) || Canonical(folder) is not { } real)
            {
                continue;
            }

            scanned.Add(new CleanFolder(real, target.Recurse, target.KeepFolders));
            foreach (CleanFile file in Enumerate(real, target, rule.MinimumAge, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(file.Path))
                {
                    files.Add(file);
                }
            }
        }

        return new RulePreview(rule) { Files = files, Folders = scanned, Profiles = free, LockedProfiles = locked, Bytes = files.Sum(f => f.Bytes) };
    }

    private IEnumerable<CleanFile> Enumerate(string folder, TargetDefinition target, TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        DateTimeOffset now = environment.Now;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = target.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        return new FileSystemEnumerable<CleanFile>(folder, (ref FileSystemEntry entry) => new CleanFile(entry.ToFullPath(), entry.Length), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !entry.IsDirectory
                    && Matches(entry.FileName.ToString(), target.Files, target.Exclude)
                    && IsOldEnough(now, minimumAge, entry.LastWriteTimeUtc.UtcDateTime, entry.CreationTimeUtc.UtcDateTime);
            },
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            },
        };
    }

    private static string? Canonical(string folder) =>
        PathSyntax.TryNormalize(folder, out string normalized) == PathVerdict.Allowed
        && Win32PathResolver.Instance.Resolve(normalized) is { Status: ResolutionStatus.Resolved, Exists: true, IsReparsePoint: false, CanonicalPath: { } canonical }
            ? canonical
            : null;
}
