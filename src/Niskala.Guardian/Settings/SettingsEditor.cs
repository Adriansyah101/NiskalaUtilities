using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Interop.Registry;
using Niskala.Interop.Services;
using Niskala.Interop.Tasks;

namespace Niskala.Guardian.Settings;

public interface IServiceStartControl
{
    int TryGetStartConfig(string name, out ServiceStartConfig config);

    int TrySetStartType(string name, ServiceStartType startType);
}

public interface ITaskStateControl
{
    int TryGetEnabled(string path, out bool enabled);

    int TrySetEnabled(string path, bool enabled);
}

public sealed class SystemServiceStartControl : IServiceStartControl
{
    public static SystemServiceStartControl Instance { get; } = new();

    public int TryGetStartConfig(string name, out ServiceStartConfig config) => ServiceNative.TryGetStartConfig(name, out config);

    public int TrySetStartType(string name, ServiceStartType startType) => ServiceNative.TrySetStartType(name, startType);
}

public sealed class SystemTaskStateControl : ITaskStateControl
{
    public static SystemTaskStateControl Instance { get; } = new();

    public int TryGetEnabled(string path, out bool enabled) => TaskSchedulerNative.TryGetEnabled(path, out enabled);

    public int TrySetEnabled(string path, bool enabled) => TaskSchedulerNative.TrySetEnabled(path, enabled);
}

public sealed class SettingsEditor(
    RegistryGuard guard,
    TransactionStore store,
    IServiceStartControl services,
    ITaskStateControl tasks,
    ILogger<SettingsEditor> logger,
    TimeProvider? time = null)
{
    public const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";
    public const string WindowsTaskFolder = @"\Microsoft\";

    private const int ErrorAccessDenied = 5;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TransactionRecord Apply(string subjectId, string? subjectName, IReadOnlyList<SettingChange> changes)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentNullException.ThrowIfNull(changes);
        var transaction = new TransactionRecord
        {
            SchemaVersion = TransactionStore.SchemaVersion,
            Id = store.NewId(),
            Kind = TransactionKind.ResourceControl,
            AppId = subjectId,
            AppName = subjectName,
            StartedAt = _time.GetUtcNow(),
            Settings = [.. changes.Select(c => c with { Status = SettingStatus.Pending })],
        };
        store.Save(transaction);
        GuardianLog.TransactionStarted(logger, transaction.Id, transaction.Kind, subjectId);

        SettingChange[] applied = [.. transaction.Settings.Select(Apply)];
        transaction = transaction with { Settings = applied, FinishedAt = _time.GetUtcNow() };
        store.Save(transaction);
        foreach (SettingChange change in applied)
        {
            GuardianLog.SettingFinished(logger, transaction.Id, change.Kind, change.Target, change.Name, change.Status, change.Detail);
        }

        return transaction;
    }

    public IReadOnlyList<SettingChange> Revert(IReadOnlyList<SettingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return [.. changes.Select(c => c.Status == SettingStatus.Applied ? Revert(c) : c)];
    }

    public static bool IsWindowsTask(string path) => path.StartsWith(WindowsTaskFolder, StringComparison.OrdinalIgnoreCase);

    private SettingChange Apply(SettingChange change) => change.Kind switch
    {
        SettingKind.RegistryValue => ApplyValue(change),
        SettingKind.ServiceStart => ApplyService(change),
        _ => ApplyTask(change),
    };

    private SettingChange Revert(SettingChange change) => change.Kind switch
    {
        SettingKind.RegistryValue => RevertValue(change),
        SettingKind.ServiceStart => RevertService(change),
        _ => RevertTask(change),
    };

    private SettingChange ApplyValue(SettingChange change)
    {
        string name = change.Name ?? string.Empty;
        RegistryDecision decision = guard.CheckValue(change.Target, name);
        if (!decision.IsAllowed)
        {
            return Skipped(change, decision.ToString());
        }

        int error = OpenKey(decision.Path!, create: change.ValueAfter is not null, out SafeRegistryHandle key);
        using (key)
        {
            if (error == RegistryNative.ErrorFileNotFound && change.ValueAfter is null)
            {
                return change with { Status = SettingStatus.Unchanged, Detail = "already absent" };
            }

            if (error != 0)
            {
                return Failed(change, error);
            }

            if (RegistryNative.IsLink(key))
            {
                return Skipped(change, "the key is a registry link");
            }

            error = ReadValue(key, name, out RawRegistryValue? before);
            if (error != 0)
            {
                return Failed(change, error);
            }

            change = change with { ValueBefore = before };
            if (Same(before, change.ValueAfter))
            {
                return change with { Status = SettingStatus.Unchanged };
            }

            error = WriteValue(key, name, change.ValueAfter);
            return error == 0 ? change with { Status = SettingStatus.Applied } : Failed(change, error);
        }
    }

    private SettingChange RevertValue(SettingChange change)
    {
        string name = change.Name ?? string.Empty;
        RegistryDecision decision = guard.CheckValue(change.Target, name);
        if (!decision.IsAllowed)
        {
            return Skipped(change, decision.ToString());
        }

        int error = OpenKey(decision.Path!, create: change.ValueBefore is not null, out SafeRegistryHandle key);
        using (key)
        {
            if (error != 0)
            {
                return error == RegistryNative.ErrorFileNotFound ? Skipped(change, "the key is gone") : Failed(change, error);
            }

            if (RegistryNative.IsLink(key))
            {
                return Skipped(change, "the key is a registry link");
            }

            error = ReadValue(key, name, out RawRegistryValue? current);
            if (error != 0)
            {
                return Failed(change, error);
            }

            if (!Same(current, change.ValueAfter))
            {
                return Skipped(change, "changed since; left as it is");
            }

            error = WriteValue(key, name, change.ValueBefore);
            return error == 0 ? change with { Status = SettingStatus.Restored, Detail = null } : Failed(change, error);
        }
    }

    private SettingChange ApplyService(SettingChange change)
    {
        RegistryDecision decision = guard.CheckKey(ServicesKey + change.Target);
        if (!decision.IsAllowed)
        {
            return Skipped(change, decision.ToString());
        }

        int error = services.TryGetStartConfig(change.Target, out ServiceStartConfig before);
        if (error != 0)
        {
            return Failed(change, error);
        }

        change = change with { StartBefore = before.StartType };
        if (before.StartType == change.StartAfter)
        {
            return change with { Status = SettingStatus.Unchanged };
        }

        error = services.TrySetStartType(change.Target, change.StartAfter!.Value);
        return error == 0 ? change with { Status = SettingStatus.Applied } : Failed(change, error);
    }

    private SettingChange RevertService(SettingChange change)
    {
        RegistryDecision decision = guard.CheckKey(ServicesKey + change.Target);
        if (!decision.IsAllowed)
        {
            return Skipped(change, decision.ToString());
        }

        int error = services.TryGetStartConfig(change.Target, out ServiceStartConfig current);
        if (error != 0)
        {
            return Failed(change, error);
        }

        if (current.StartType != change.StartAfter)
        {
            return Skipped(change, "changed since; left as it is");
        }

        error = services.TrySetStartType(change.Target, change.StartBefore!.Value);
        return error == 0 ? change with { Status = SettingStatus.Restored, Detail = null } : Failed(change, error);
    }

    private SettingChange ApplyTask(SettingChange change)
    {
        if (IsWindowsTask(change.Target))
        {
            return Skipped(change, "a Windows task");
        }

        int error = tasks.TryGetEnabled(change.Target, out bool before);
        if (error != 0)
        {
            return Failed(change, error);
        }

        change = change with { EnabledBefore = before };
        if (before == change.EnabledAfter)
        {
            return change with { Status = SettingStatus.Unchanged };
        }

        error = tasks.TrySetEnabled(change.Target, change.EnabledAfter!.Value);
        return error == 0 ? change with { Status = SettingStatus.Applied } : Failed(change, error);
    }

    private SettingChange RevertTask(SettingChange change)
    {
        if (IsWindowsTask(change.Target))
        {
            return Skipped(change, "a Windows task");
        }

        int error = tasks.TryGetEnabled(change.Target, out bool current);
        if (error != 0)
        {
            return Failed(change, error);
        }

        if (current != change.EnabledAfter)
        {
            return Skipped(change, "changed since; left as it is");
        }

        error = tasks.TrySetEnabled(change.Target, change.EnabledBefore!.Value);
        return error == 0 ? change with { Status = SettingStatus.Restored, Detail = null } : Failed(change, error);
    }

    private static int OpenKey(RegistryPath path, bool create, out SafeRegistryHandle key) => create
        ? RegistryNative.TryCreateKey(RegistryNative.Hive(path.Hive), path.PhysicalSubKey, path.View == Microsoft.Win32.RegistryView.Registry32, out key)
        : RegistryEraser.TryOpen(path, writable: true, out key);

    private static int ReadValue(SafeRegistryHandle key, string name, out RawRegistryValue? value)
    {
        int error = RegistryNative.TryReadValue(key, name, out value);
        if (error == RegistryNative.ErrorFileNotFound)
        {
            value = null;
            return 0;
        }

        return error;
    }

    private static int WriteValue(SafeRegistryHandle key, string name, RawRegistryValue? value)
    {
        if (value is null)
        {
            int error = RegistryNative.TryDeleteValue(key, name);
            return error == RegistryNative.ErrorFileNotFound ? 0 : error;
        }

        return RegistryNative.TrySetValue(key, value with { Name = name });
    }

    private static bool Same(RawRegistryValue? a, RawRegistryValue? b) => (a, b) switch
    {
        (null, null) => true,
        ({ } x, { } y) => x.Kind == y.Kind && x.Data.AsSpan().SequenceEqual(y.Data),
        _ => false,
    };

    private static SettingChange Skipped(SettingChange change, string detail) => change with { Status = SettingStatus.Skipped, Detail = detail };

    private static SettingChange Failed(SettingChange change, int error) => change with
    {
        Status = SettingStatus.Failed,
        Detail = error == ErrorAccessDenied || error == TaskSchedulerNative.AccessDenied
            ? "access denied; this change needs elevation"
            : string.Create(CultureInfo.InvariantCulture, $"error 0x{error:X8}"),
    };
}
