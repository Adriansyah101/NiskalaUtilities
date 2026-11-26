using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Guardian.Settings;
using Niskala.Interop.Firewall;
using Niskala.Interop.Tasks;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Restore;

public sealed record RestoreReport(
    TransactionRecord Transaction,
    int ValuesWritten,
    int ValuesKept,
    int ItemsRestored,
    int RulesRestored,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes);

public sealed class TransactionRestorer(
    TransactionStore store,
    IFirewallEditor firewall,
    IScheduledTaskEditor tasks,
    ILogger<TransactionRestorer> logger,
    TimeProvider? time = null,
    SettingsEditor? settings = null)
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RestoreReport? Restore(string id)
    {
        TransactionRecord? transaction = store.Load(id);
        if (transaction is null)
        {
            return null;
        }

        if (transaction.RestoredAt is { } restoredAt)
        {
            return new RestoreReport(transaction, 0, 0, 0, 0, [string.Create(CultureInfo.InvariantCulture, $"already restored on {restoredAt.ToLocalTime():yyyy-MM-dd HH:mm}")], []);
        }

        var problems = new List<string>();
        var notes = new List<string>();
        (int written, int kept) = RestoreRegistry(transaction, problems, notes);

        int restored = 0;
        var items = transaction.Items.ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            RemovalRecord item = items[i];
            if (item.Status != RemovalStatus.Removed)
            {
                continue;
            }

            bool back = item switch
            {
                { RecycledPath: { } recycled } => RestoreRecycled(item, recycled, problems),
                { EmptyFolder: true } => RecreateFolder(item.Path, problems),
                { Kind: LeftoverKind.RegistryKey or LeftoverKind.RegistryValue } => transaction.HasRegistryBackup,
                _ => false,
            };

            if (back)
            {
                items[i] = item with { Status = RemovalStatus.Restored };
                restored++;
            }
        }

        IReadOnlyList<SettingChange> changes = RestoreSettings(transaction, problems);
        restored += changes.Count(c => c.Status == SettingStatus.Restored);

        int rules = RestoreRules(transaction, items, problems);
        restored += items.Count(i => i.Kind == LeftoverKind.FirewallRule && i.Status == RemovalStatus.Restored);
        restored += RestoreTasks(transaction, items, problems);

        int permanent = items.Count(i => i is { Status: RemovalStatus.Removed or RemovalStatus.ScheduledAtReboot, Kind: LeftoverKind.File or LeftoverKind.Folder, RecycledPath: null, EmptyFolder: false });
        if (permanent > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{permanent} files or folders were deleted permanently and cannot be restored."));
        }

        int reverted = items.Count(i => i.Status == RemovalStatus.Reverted);
        if (reverted > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{reverted} registry values written back to their earlier data stay that way."));
        }

        if (transaction.Kind == TransactionKind.Uninstall)
        {
            notes.Add("Only the registry backup comes back; the uninstaller removed the program files.");
        }

        if (transaction.RestorePoint is { Created: true, SequenceNumber: { } sequence })
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"System Restore point #{sequence} can roll back the rest: run rstrui.exe."));
        }

        transaction = transaction with { Items = items, Settings = changes, RestoredAt = _time.GetUtcNow() };
        store.Save(transaction);
        GuardianLog.TransactionRestored(logger, transaction.Id, written, restored, rules);
        return new RestoreReport(transaction, written, kept, restored, rules, problems, notes);
    }

    private IReadOnlyList<SettingChange> RestoreSettings(TransactionRecord transaction, List<string> problems)
    {
        if (transaction.Settings.Count == 0)
        {
            return transaction.Settings;
        }

        if (settings is null)
        {
            problems.Add("this transaction changed settings; restoring them needs the settings editor");
            return transaction.Settings;
        }

        IReadOnlyList<SettingChange> reverted = settings.Revert(transaction.Settings);
        for (int i = 0; i < reverted.Count; i++)
        {
            if (transaction.Settings[i].Status == SettingStatus.Applied && reverted[i].Status != SettingStatus.Restored)
            {
                problems.Add($"{reverted[i].Label ?? reverted[i].Target}: {reverted[i].Detail}");
            }
        }

        return reverted;
    }

    private (int Written, int Kept) RestoreRegistry(TransactionRecord transaction, List<string> problems, List<string> notes)
    {
        if (!transaction.HasRegistryBackup || store.LoadRegistryBackup(transaction.Id) is not { } backup)
        {
            return (0, 0);
        }

        int written = 0;
        int kept = 0;
        foreach (RegistryBackupEntry entry in backup.Entries)
        {
            if (RegistryPath.TryParse(entry.Path, RegistryView.Default, out RegistryPath? path) != RegistryVerdict.Allowed)
            {
                problems.Add($"{entry.Path}: not a key path");
                continue;
            }

            int error;
            if (entry.Key is { } key)
            {
                error = RegistryEraser.TryRestore(path!, key, out RestoreCounts counts);
                written += counts.Written;
                kept += counts.Kept;
            }
            else
            {
                error = RegistryEraser.TryRestoreValue(path!, entry.Value!, out bool wrote);
                written += wrote ? 1 : 0;
                kept += wrote ? 0 : 1;
            }

            if (error != 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{entry.Path}: Win32 error {error}"));
            }
        }

        if (backup.Entries.Any(e => e.Key is not null && e.Path.StartsWith(ServicesKey, StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add("Restored services are registered again after Windows restarts.");
        }

        return (written, kept);
    }

    private static bool RestoreRecycled(RemovalRecord item, string recycled, List<string> problems)
    {
        bool isFolder = item.Kind == LeftoverKind.Folder;
        if (isFolder ? !Directory.Exists(recycled) : !File.Exists(recycled))
        {
            problems.Add($"{item.Path}: no longer in the Recycle Bin");
            return false;
        }

        if (File.Exists(item.Path) || Directory.Exists(item.Path))
        {
            problems.Add($"{item.Path}: exists again; the old one stays in the Recycle Bin");
            return false;
        }

        try
        {
            if (Path.GetDirectoryName(item.Path) is { } parent)
            {
                Directory.CreateDirectory(parent);
            }

            if (isFolder)
            {
                Directory.Move(recycled, item.Path);
            }
            else
            {
                File.Move(recycled, item.Path);
            }

            string name = Path.GetFileName(recycled);
            if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(recycled)!, "$I" + name[2..]));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{item.Path}: {ex.Message}");
            return false;
        }
    }

    private static bool RecreateFolder(string path, List<string> problems)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: {ex.Message}");
            return false;
        }
    }

    private int RestoreTasks(TransactionRecord transaction, RemovalRecord[] items, List<string> problems)
    {
        int registered = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is not { Kind: LeftoverKind.ScheduledTask, Status: RemovalStatus.Removed } item)
            {
                continue;
            }

            if (transaction.ScheduledTasks.FirstOrDefault(t => t.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) is not { } definition)
            {
                problems.Add($"task {item.Path}: no definition was saved");
                continue;
            }

            int error = tasks.TryRegister(definition.Path, definition.Xml);
            if (error == 0)
            {
                items[i] = item with { Status = RemovalStatus.Restored };
                registered++;
            }
            else
            {
                problems.Add(error == TaskSchedulerNative.AlreadyExists
                    ? $"task {item.Path}: a task holds that path now; left as it is"
                    : string.Create(CultureInfo.InvariantCulture, $"task {item.Path}: 0x{error:X8}"));
            }
        }

        return registered;
    }

    private int RestoreRules(TransactionRecord transaction, RemovalRecord[] items, List<string> problems)
    {
        var removed = new HashSet<string>(
            items.Where(i => i.Kind == LeftoverKind.FirewallRule && i.Status == RemovalStatus.Removed).Select(i => i.Path),
            StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0)
        {
            return 0;
        }

        int error = firewall.TryList(out List<FirewallRule> current);
        if (error != 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"firewall rules could not be read (0x{error:X8})"));
            return 0;
        }

        int added = 0;
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FirewallRule rule in transaction.FirewallRules.Where(r => removed.Contains(r.Name)))
        {
            if (current.Exists(c => c.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.ApplicationName, rule.ApplicationName, StringComparison.OrdinalIgnoreCase)
                && c.Direction == rule.Direction
                && c.Protocol == rule.Protocol))
            {
                continue;
            }

            error = firewall.TryAdd(rule);
            if (error == 0)
            {
                added++;
            }
            else
            {
                failed.Add(rule.Name);
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"firewall rule \"{rule.Name}\": 0x{error:X8}"));
            }
        }

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Kind == LeftoverKind.FirewallRule && items[i].Status == RemovalStatus.Removed && !failed.Contains(items[i].Path))
            {
                items[i] = items[i] with { Status = RemovalStatus.Restored };
            }
        }

        return added;
    }
}
