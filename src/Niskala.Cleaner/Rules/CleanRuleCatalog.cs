using System.Text.Json;
using System.Text.RegularExpressions;

namespace Niskala.Cleaner.Rules;

public sealed record RuleProblem(string Source, string Message);

public sealed partial class CleanRuleCatalog
{
    public const int SchemaVersion = 1;
    public const string FolderName = "rules";

    private static readonly LocalizedText CompactName = new() { En = "Compact databases", Id = "Padatkan basis data" };
    private static readonly LocalizedText CompactDescription = new()
    {
        En = "Checks each SQLite database with PRAGMA integrity_check, then rebuilds it with VACUUM; nothing is deleted.",
        Id = "Memeriksa tiap basis data SQLite dengan PRAGMA integrity_check, lalu menyusunnya ulang dengan VACUUM; tidak ada yang dihapus.",
    };

    private CleanRuleCatalog(IReadOnlyList<CleanRule> rules, IReadOnlyList<RuleProblem> problems)
    {
        Rules = rules;
        Problems = problems;
    }

    public IReadOnlyList<CleanRule> Rules { get; }

    public IReadOnlyList<RuleProblem> Problems { get; }

    public static string DefaultDirectory
    {
        get
        {
            string beside = Path.Combine(AppContext.BaseDirectory, FolderName);
            string library = Path.Combine(AppContext.BaseDirectory, "lib", FolderName);
            return Directory.Exists(beside) || !Directory.Exists(library) ? beside : library;
        }
    }

    public static CleanRuleCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            return new CleanRuleCatalog([], [new RuleProblem(directory, "the rules folder is missing")]);
        }

        var files = new List<(string, string)>();
        var problems = new List<RuleProblem>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                files.Add((Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new RuleProblem(Path.GetFileName(file), ex.Message));
            }
        }

        CleanRuleCatalog parsed = Parse(files);
        return problems.Count == 0 ? parsed : new CleanRuleCatalog(parsed.Rules, [.. problems, .. parsed.Problems]);
    }

    public static CleanRuleCatalog Parse(IEnumerable<(string Source, string Json)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var problems = new List<RuleProblem>();
        var definitions = new List<(string Source, RuleDefinition Rule)>();
        var browsers = new List<(string Source, BrowserDefinition Browser)>();
        var engines = new Dictionary<BrowserEngine, EngineDefinition>();

        foreach ((string source, string json) in files)
        {
            RuleFile? file;
            try
            {
                file = JsonSerializer.Deserialize(json, RuleJsonContext.Default.RuleFile) is { } read ? Normalize(read) : null;
            }
            catch (JsonException ex)
            {
                problems.Add(new RuleProblem(source, ex.Message));
                continue;
            }

            if (file is null || file.SchemaVersion != SchemaVersion)
            {
                problems.Add(new RuleProblem(source, $"schemaVersion must be {SchemaVersion}"));
                continue;
            }

            definitions.AddRange(file.Rules.Select(r => (source, r)));
            browsers.AddRange(file.Browsers.Select(b => (source, b)));
            foreach (EngineDefinition engine in file.Engines)
            {
                if (!engines.TryAdd(engine.Engine, engine))
                {
                    problems.Add(new RuleProblem(source, $"engine {engine.Engine} is defined twice"));
                }
            }
        }

        var rules = new List<CleanRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string source, RuleDefinition definition) in definitions)
        {
            if (Validate(definition) is { } problem)
            {
                problems.Add(new RuleProblem(source, $"{definition.Id}: {problem}"));
                continue;
            }

            Add(source, new CleanRule
            {
                Id = definition.Id,
                Category = definition.Category,
                Name = definition.Name,
                Description = definition.Description,
                Quick = definition.Quick,
                Removal = definition.Removal,
                RequiresElevation = definition.RequiresElevation,
                MinimumAge = TimeSpan.FromHours(definition.MinimumAgeHours),
                Targets = definition.Targets,
            });
        }

        foreach ((string source, BrowserDefinition browser) in browsers)
        {
            if (ValidateBrowser(browser) is { } problem)
            {
                problems.Add(new RuleProblem(source, $"{browser.Id}: {problem}"));
                continue;
            }

            if (!engines.TryGetValue(browser.Engine, out EngineDefinition? engine))
            {
                problems.Add(new RuleProblem(source, $"{browser.Id}: no items for engine {browser.Engine}"));
                continue;
            }

            foreach (EngineItemDefinition item in engine.Items)
            {
                if (Validate(item) is { } itemProblem)
                {
                    problems.Add(new RuleProblem(source, $"{browser.Id}.{item.Id}: {itemProblem}"));
                    continue;
                }

                Add(source, new CleanRule
                {
                    Id = $"browser.{browser.Id}.{item.Id}",
                    Category = item.Category,
                    Name = item.Name,
                    Description = item.Description,
                    Quick = item.Quick,
                    Removal = item.Removal,
                    Targets = item.Targets,
                    Browser = browser,
                    ProfileMarker = engine.ProfileMarker,
                    Area = item.Area,
                });
            }

            if (engine.Databases.Count > 0)
            {
                if (engine.Databases.FirstOrDefault(d => ValidateRelative(d) is not null) is { } bad)
                {
                    problems.Add(new RuleProblem(source, $"{browser.Id}: database path {bad} is not a plain relative path"));
                    continue;
                }

                Add(source, new CleanRule
                {
                    Id = $"browser.{browser.Id}.compact",
                    Category = CleanCategory.DatabaseCompaction,
                    Name = CompactName,
                    Description = CompactDescription,
                    Action = RuleAction.Compact,
                    Browser = browser,
                    ProfileMarker = engine.ProfileMarker,
                    Databases = engine.Databases,
                });
            }
        }

        return new CleanRuleCatalog(rules, problems);

        void Add(string source, CleanRule rule)
        {
            if (ids.Add(rule.Id))
            {
                rules.Add(rule);
            }
            else
            {
                problems.Add(new RuleProblem(source, $"{rule.Id}: the id is used twice"));
            }
        }
    }

    public CleanRule? Find(string id) => Rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CleanRule> Select(IEnumerable<string> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        var chosen = new List<CleanRule>();
        foreach (string selector in selectors)
        {
            IEnumerable<CleanRule> matches = Enum.TryParse(selector, ignoreCase: true, out CleanCategory category) && !int.TryParse(selector, out _)
                ? Rules.Where(r => r.Category == category)
                : selector.EndsWith('*')
                    ? Rules.Where(r => r.Id.StartsWith(selector[..^1], StringComparison.OrdinalIgnoreCase))
                    : Rules.Where(r => r.Id.Equals(selector, StringComparison.OrdinalIgnoreCase));
            chosen.AddRange(matches.Where(r => !chosen.Contains(r)));
        }

        return chosen;
    }

    private static RuleFile Normalize(RuleFile file) => file with
    {
        Rules = [.. (file.Rules ?? []).Select(r => r with { Targets = [.. (r.Targets ?? []).Select(Normalize)] })],
        Browsers = [.. (file.Browsers ?? []).Select(b => b with { Processes = b.Processes ?? [], UserData = b.UserData ?? [], CacheData = b.CacheData ?? [] })],
        Engines = [.. (file.Engines ?? []).Select(e => e with
        {
            Items = [.. (e.Items ?? []).Select(i => i with { Targets = [.. (i.Targets ?? []).Select(Normalize)] })],
            Databases = e.Databases ?? [],
        })],
    };

    private static TargetDefinition Normalize(TargetDefinition target) => target with
    {
        Path = target.Path ?? string.Empty,
        Files = target.Files ?? ["*"],
        Exclude = target.Exclude ?? [],
    };

    internal static string? ValidateRelative(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(path) || path.Contains('/') || path.Contains(':'))
        {
            return "must be relative, with backslashes";
        }

        foreach (string segment in path.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(['*', '?']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return $"segment \"{segment}\" is not a plain folder name";
            }
        }

        return null;
    }

    private static string? Validate(RuleDefinition rule)
    {
        if (!IdPattern().IsMatch(rule.Id) || rule.Id.StartsWith("browser.", StringComparison.OrdinalIgnoreCase))
        {
            return "ids are lower-case words joined by '.' or '-', and browser.* is reserved";
        }

        if (rule.MinimumAgeHours < 0)
        {
            return "minimumAgeHours cannot be negative";
        }

        return ValidateTargets(rule.Targets, needsRoot: true);
    }

    private static string? Validate(EngineItemDefinition item) =>
        !IdPattern().IsMatch(item.Id) || item.Id.Contains('.') ? "item ids are lower-case words joined by '-'" : ValidateTargets(item.Targets, needsRoot: false);

    private static string? ValidateTargets(IReadOnlyList<TargetDefinition> targets, bool needsRoot)
    {
        if (targets.Count == 0)
        {
            return "needs at least one target";
        }

        foreach (TargetDefinition target in targets)
        {
            if (needsRoot && target.Root is null)
            {
                return "every target names a root";
            }

            if (!needsRoot && target.Root is not null)
            {
                return "browser targets are relative to the profile and name no root";
            }

            if (target.Root is CleanRoot.Windows or CleanRoot.ProgramData && target.Path.Length == 0)
            {
                return $"a {target.Root} target names a sub-folder";
            }

            if (ValidateRelative(target.Path) is { } problem)
            {
                return $"path {target.Path}: {problem}";
            }

            if (target.Files.Count == 0 || target.Files.Concat(target.Exclude).Any(p => p.Length == 0 || p.IndexOfAny(['\\', '/', ':']) >= 0 || p is "." or ".."))
            {
                return "file patterns are names with * or ? only";
            }
        }

        return null;
    }

    private static string? ValidateBrowser(BrowserDefinition browser)
    {
        if (!IdPattern().IsMatch(browser.Id) || browser.Id.Contains('.'))
        {
            return "browser ids are lower-case words joined by '-'";
        }

        if (browser.UserData.Count == 0)
        {
            return "needs at least one userData location";
        }

        if (browser.UserData.Concat(browser.CacheData).FirstOrDefault(l => l.Path.Length == 0 || ValidateRelative(l.Path) is not null) is { } bad)
        {
            return $"location {bad.Path} is not a plain relative path";
        }

        return browser.Processes.Any(p => p.Length == 0 || p.IndexOfAny(['\\', '/', '.']) >= 0) ? "process names have no extension or folder" : null;
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
