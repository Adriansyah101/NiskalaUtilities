using Niskala.Core.Safety;
using Niskala.Guardian.Startup;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Picker;

public sealed class PickerActions(IProcessControl processes)
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    public IReadOnlyList<RunningProcess> Running(PickerHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        IReadOnlyList<RunningProcess> all = processes.List();
        if (hit.ProcessId != 0)
        {
            return [.. all.Where(p => p.Id == hit.ProcessId && p.ImagePath is not null)];
        }

        string? root = InstallRoot(hit);
        return [.. all.Where(p => Starts(hit, root, p.ImagePath))];
    }

    public IReadOnlyList<ProcessStop> Stop(IReadOnlyList<RunningProcess> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return [.. running.Select(p => new ProcessStop(p, processes.Terminate(p, StopWait)))];
    }

    public static IReadOnlyList<StartupEntry> StartupEntries(PickerHit hit, IReadOnlyList<StartupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(entries);
        string? root = InstallRoot(hit);
        return [.. entries.Where(e => e.Enabled && Starts(hit, root, e.TargetPath))];
    }

    private static string? InstallRoot(PickerHit hit) =>
        hit.App is { InstallLocationGuard: PathVerdict.Allowed } app && PathSyntax.TryNormalize(app.InstallLocation, out string root) == PathVerdict.Allowed
            ? root
            : null;

    private static bool Starts(PickerHit hit, string? root, string? program)
    {
        if (program is null || PathSyntax.TryNormalize(program, out string normalized) != PathVerdict.Allowed)
        {
            return false;
        }

        return (hit.ImagePath is not null && normalized.Equals(hit.ImagePath, StringComparison.OrdinalIgnoreCase))
            || (root is not null && PathSyntax.IsSameOrUnder(normalized, root, out _));
    }
}
