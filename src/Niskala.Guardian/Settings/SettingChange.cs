using Niskala.Interop.Registry;
using Niskala.Interop.Services;

namespace Niskala.Guardian.Settings;

public enum SettingKind
{
    RegistryValue,
    ServiceStart,
    TaskEnabled,
}

public enum SettingStatus
{
    Pending,
    Applied,
    Unchanged,
    Skipped,
    Failed,
    Restored,
}

public sealed record SettingChange(SettingKind Kind, string Target, string? Name = null)
{
    public string? Label { get; init; }

    public RawRegistryValue? ValueBefore { get; init; }

    public RawRegistryValue? ValueAfter { get; init; }

    public ServiceStartType? StartBefore { get; init; }

    public ServiceStartType? StartAfter { get; init; }

    public bool? EnabledBefore { get; init; }

    public bool? EnabledAfter { get; init; }

    public SettingStatus Status { get; init; }

    public string? Detail { get; init; }

    public static SettingChange Value(string key, string name, RawRegistryValue? after, string? label = null) =>
        new(SettingKind.RegistryValue, key, name) { ValueAfter = after, Label = label };

    public static SettingChange Service(string name, ServiceStartType after, string? label = null) =>
        new(SettingKind.ServiceStart, name) { StartAfter = after, Label = label };

    public static SettingChange Task(string path, bool enabled, string? label = null) =>
        new(SettingKind.TaskEnabled, path) { EnabledAfter = enabled, Label = label };
}
