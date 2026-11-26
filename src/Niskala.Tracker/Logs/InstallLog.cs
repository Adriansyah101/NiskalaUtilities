namespace Niskala.Tracker.Logs;

public enum ChangeKind
{
    Created,
    Modified,
    Deleted,
}

public sealed record FileChange(string Path, ChangeKind Change, bool IsDirectory);

public sealed record KeyChange(string Path, ChangeKind Change)
{
    public string? Origin { get; init; }
}

public sealed record ValueChange(string Key, string Name, ChangeKind Change)
{
    public int? Kind { get; init; }

    public byte[]? Data { get; init; }

    public int? PreviousKind { get; init; }

    public byte[]? PreviousData { get; init; }

    public bool RevertOnly { get; init; }
}

public sealed record FirewallRuleChange(string Name, string? ApplicationName);

public sealed record RecordedProcess(int Id, int ParentId, string? ImagePath, DateTimeOffset StartedAt, bool Adopted)
{
    public DateTimeOffset? ExitedAt { get; init; }
}

public sealed record TraceStatistics(long EventsSeen, long EventsRecorded, int EventsLost, int BuffersLost, int PendingDropped, int UnnamedEvents, int UnmappedPaths);

public sealed record InstallLog
{
    public const string FormatName = "nsklog";

    public required string Format { get; init; }

    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Installer { get; init; }

    public string? Arguments { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public int? ExitCode { get; init; }

    public string? UninstallKey { get; init; }

    public string? AppId { get; init; }

    public IReadOnlyList<RecordedProcess> Processes { get; init; } = [];

    public IReadOnlyList<FileChange> Files { get; init; } = [];

    public IReadOnlyList<KeyChange> Keys { get; init; } = [];

    public IReadOnlyList<ValueChange> Values { get; init; } = [];

    public IReadOnlyList<FirewallRuleChange> FirewallRules { get; init; } = [];

    public IReadOnlyDictionary<string, int> Filtered { get; init; } = new Dictionary<string, int>();

    public TraceStatistics? Statistics { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? RollbackTransactionId { get; init; }

    public DateTimeOffset? RolledBackAt { get; init; }

    public bool UsesWindowsInstaller() =>
        Processes.Any(p => string.Equals(Path.GetFileName(p.ImagePath), "msiexec.exe", StringComparison.OrdinalIgnoreCase));
}
