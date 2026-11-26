namespace Niskala.Scanner.Fingerprints;

public sealed record FileTreeEntry(string RelativePath, bool IsDirectory, bool IsReparsePoint, long Size, DateTime LastWriteUtc);

public sealed record FileTreeSnapshot(string Root, IReadOnlyList<FileTreeEntry> Entries, int Files, int Directories, long Bytes, bool Truncated);

public sealed record ServiceTrace(string Name, string? DisplayName, string ImagePath, string? ServiceDll, int? StartType);

public sealed record RegistryValueTrace(string Key, string? ValueName, string Data);

public sealed record TaskTrace(string Path, string Command, string? Arguments);

public sealed record ShortcutTrace(string LinkPath, string? Target, string? Arguments, Guid? MsiProductCode);

public sealed record Fingerprint
{
    public required string AppId { get; init; }

    public required DateTimeOffset TakenAt { get; init; }

    public TimeSpan Elapsed { get; init; }

    public IReadOnlyList<string> Roots { get; init; } = [];

    public FileTreeSnapshot? Files { get; init; }

    public IReadOnlyList<string> ExecutableNames { get; init; } = [];

    public Guid? MsiProductCode { get; init; }

    public Guid? MsiUpgradeCode { get; init; }

    public string? InnoAppId { get; init; }

    public IReadOnlyList<ServiceTrace> Services { get; init; } = [];

    public IReadOnlyList<RegistryValueTrace> RunEntries { get; init; } = [];

    public IReadOnlyList<RegistryValueTrace> AppPaths { get; init; } = [];

    public IReadOnlyList<TaskTrace> ScheduledTasks { get; init; } = [];

    public IReadOnlyList<ShortcutTrace> Shortcuts { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}
