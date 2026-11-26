using Niskala.Core.Models;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;

namespace Niskala.Optimizer.Resources;

public enum ResourceKind
{
    RunEntry,
    RunOnceEntry,
    StartupFolder,
    Service,
    ScheduledTask,
    ContextMenu,
}

public sealed record ResourceItem
{
    public required string Id { get; init; }

    public required ResourceKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Location { get; init; }

    public required string Target { get; init; }

    public string? ValueName { get; init; }

    public string? Command { get; init; }

    public string? Program { get; init; }

    public string? Publisher { get; init; }

    public InstallScope Scope { get; init; }

    public bool Enabled { get; init; }

    public BootImpact Impact { get; init; }

    public ServiceStartType? StartType { get; init; }

    public bool DelayedStart { get; init; }

    public bool RunsAtSignIn { get; init; }

    public RawRegistryValue? ParkedValue { get; init; }

    public string? Blocked { get; init; }

    public bool CanToggle => Blocked is null;
}
