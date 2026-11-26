using Niskala.Guardian.Settings;
using Niskala.Interop.Firewall;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Journal;

public enum TransactionKind
{
    Uninstall,
    Clean,
    Force,
    Rollback,
    SystemClean,
    RegistryIntegrity,
    ResourceControl,
    DuplicateClean,
}

public enum RemovalMethod
{
    RecycleBin,
    Permanent,
}

public enum RemovalStatus
{
    Pending,
    Removed,
    ScheduledAtReboot,
    Skipped,
    Failed,
    Restored,
    Reverted,
    Compacted,
}

public sealed record RemovalRecord(LeftoverKind Kind, string Path, string? ValueName, RemovalStatus Status)
{
    public string? RecycledPath { get; init; }

    public string? Detail { get; init; }

    public int? Score { get; init; }

    public bool EmptyFolder { get; init; }

    public int? Files { get; init; }

    public long? Bytes { get; init; }
}

public sealed record RestorePointRecord(bool Created, int? SequenceNumber, string Description, string? Note);

public sealed record TransactionRecord
{
    public required int SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required TransactionKind Kind { get; init; }

    public required string AppId { get; init; }

    public string? AppName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public RemovalMethod Method { get; init; }

    public RestorePointRecord? RestorePoint { get; init; }

    public bool HasRegistryBackup { get; init; }

    public IReadOnlyList<FirewallRule> FirewallRules { get; init; } = [];

    public IReadOnlyList<ScheduledTaskXml> ScheduledTasks { get; init; } = [];

    public IReadOnlyList<RemovalRecord> Items { get; init; } = [];

    public IReadOnlyList<SettingChange> Settings { get; init; } = [];

    public DateTimeOffset? RestoredAt { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}
