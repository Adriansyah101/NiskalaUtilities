using System.Text;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Settings;
using Niskala.Guardian.Startup;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;

namespace Niskala.Optimizer.Resources;

public sealed record ResourceChange(ResourceItem Item, bool Enabled, TransactionRecord Transaction)
{
    public SettingChange Setting => Transaction.Settings[0];

    public bool Succeeded => Setting.Status is SettingStatus.Applied or SettingStatus.Unchanged;
}

public sealed class ResourceControl(
    ResourceLocations locations,
    StartupCatalog startup,
    RegistryGuard guard,
    SettingsEditor editor,
    TransactionStore store,
    ITaskSource tasks,
    IProgramFacts programs,
    ICommandEnvironment environment,
    TimeProvider? time = null)
{
    public const string SubjectId = "resource-control";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IReadOnlyList<ResourceItem> List(IReadOnlyCollection<ResourceKind>? kinds = null)
    {
        kinds ??= Enum.GetValues<ResourceKind>();
        IReadOnlyList<SettingChange> applied = Applied();
        var items = new List<ResourceItem>();
        if (kinds.Contains(ResourceKind.RunEntry) || kinds.Contains(ResourceKind.StartupFolder) || kinds.Contains(ResourceKind.RunOnceEntry))
        {
            items.AddRange(StartupSource.List(startup, locations.RunOnce, programs, environment));
            items.AddRange(ParkedRunOnce(applied, items));
        }

        if (kinds.Contains(ResourceKind.Service))
        {
            items.AddRange(ServiceSource.List(locations.ServicesKey, guard, programs, environment.WindowsDirectory));
        }

        if (kinds.Contains(ResourceKind.ScheduledTask))
        {
            items.AddRange(TaskSource.List(tasks, programs, environment));
        }

        if (kinds.Contains(ResourceKind.ContextMenu))
        {
            items.AddRange(ContextMenuSource.List(locations, programs, environment));
        }

        return [.. items.Where(i => kinds.Contains(i.Kind))
            .OrderBy(i => i.Kind)
            .ThenByDescending(i => i.Impact)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public ResourceChange? SetEnabled(ResourceItem item, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Enabled == enabled)
        {
            return null;
        }

        if (!item.CanToggle)
        {
            throw new InvalidOperationException($"{item.Name} cannot be switched: {item.Blocked}");
        }

        SettingChange change = item.Kind switch
        {
            ResourceKind.RunEntry or ResourceKind.StartupFolder => SettingChange.Value(
                item.Target,
                item.ValueName!,
                new RawRegistryValue(item.ValueName!, (int)RegistryValueKind.Binary, StartupCatalog.ApprovedData(enabled, _time.GetUtcNow())),
                item.Name),
            ResourceKind.RunOnceEntry => SettingChange.Value(item.Target, item.ValueName!, enabled ? item.ParkedValue ?? throw new InvalidOperationException($"{item.Name} has no saved value to put back.") : null, item.Name),
            ResourceKind.Service => SettingChange.Service(item.Target, enabled ? PreviousStart(item.Target) : ServiceStartType.Disabled, item.Name),
            ResourceKind.ScheduledTask => SettingChange.Task(item.Target, enabled, item.Name),
            _ => SettingChange.Value(item.Target, item.ValueName!, enabled ? null : BlockedMarker(item.ValueName!), item.Name),
        };

        TransactionRecord transaction = editor.Apply(SubjectId, item.Name, [change]);
        return new ResourceChange(item, enabled, transaction);
    }

    private static RawRegistryValue BlockedMarker(string clsid) =>
        new(clsid, (int)RegistryValueKind.String, Encoding.Unicode.GetBytes("\0"));

    private ServiceStartType PreviousStart(string service) =>
        Applied().FirstOrDefault(c => c.Kind == SettingKind.ServiceStart
                && c.Target.Equals(service, StringComparison.OrdinalIgnoreCase)
                && c.StartAfter == ServiceStartType.Disabled
                && c.StartBefore is not (null or ServiceStartType.Disabled))?.StartBefore
            ?? ServiceStartType.Manual;

    private IReadOnlyList<SettingChange> Applied() =>
        [.. store.List()
            .Where(t => t.Kind == TransactionKind.ResourceControl && t.RestoredAt is null)
            .SelectMany(t => t.Settings.Where(s => s.Status == SettingStatus.Applied))];

    private IEnumerable<ResourceItem> ParkedRunOnce(IReadOnlyList<SettingChange> applied, List<ResourceItem> present)
    {
        var seen = new HashSet<string>(present.Where(i => i.Kind == ResourceKind.RunOnceEntry).Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        foreach (SettingChange change in applied)
        {
            if (change is not { Kind: SettingKind.RegistryValue, ValueAfter: null, ValueBefore: { } before, Name: { } name }
                || locations.RunOnce.FirstOrDefault(l => l.Key.Equals(change.Target, StringComparison.OrdinalIgnoreCase)) is not { } location
                || !seen.Add($"runonce|{location.Key}|{name}"))
            {
                continue;
            }

            string command = before.Kind is (int)RegistryValueKind.String or (int)RegistryValueKind.ExpandString
                ? Encoding.Unicode.GetString(before.Data).TrimEnd('\0')
                : string.Empty;
            string? executable = CommandLineParser.Parse(command, environment).Executable;
            yield return StartupSource.RunOnceItem(location, name, command, executable, executable is null ? null : programs.Describe(executable), enabled: false, parked: before);
        }
    }
}
