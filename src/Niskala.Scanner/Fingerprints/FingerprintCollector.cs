using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;

namespace Niskala.Scanner.Fingerprints;

public sealed record FingerprintOptions
{
    public int MaxFileEntries { get; init; } = 50_000;
}

public sealed class FingerprintCollector(
    PathGuard guard,
    ISystemTraceSource system,
    ICommandEnvironment environment,
    ILogger<FingerprintCollector> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Fingerprint Collect(InstalledApp app, FingerprintOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        options ??= new FingerprintOptions();
        long start = Stopwatch.GetTimestamp();
        var notes = new List<string>();

        (IReadOnlyList<string> roots, string? walkRoot) = Roots(app, notes);
        FileTreeSnapshot? files = null;
        IReadOnlyList<string> executables = [];
        if (walkRoot is not null)
        {
            (files, executables) = FileTreeWalker.Walk(walkRoot, options.MaxFileEntries, cancellationToken);
            if (files.Truncated)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"File list truncated to {files.Entries.Count} of {files.Files + files.Directories} entries."));
            }
        }

        var owner = new OwnershipMatcher(roots, environment);
        Guid? productCode = app.MsiProductCode;
        bool anyRoot = roots.Count > 0;

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ServiceTrace> services = anyRoot
            ? [.. system.Services().Where(s => owner.OwnsPath(s.ImagePath) || owner.OwnsPath(s.ServiceDll))]
            : [];
        IReadOnlyList<RegistryValueTrace> runEntries = anyRoot ? [.. system.RunEntries().Where(v => owner.OwnsCommand(v.Data))] : [];
        IReadOnlyList<RegistryValueTrace> appPaths = anyRoot ? [.. system.AppPaths().Where(v => owner.OwnsCommand(v.Data))] : [];

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TaskTrace> tasks = [];
        if (anyRoot)
        {
            TaskList list = system.ScheduledTasks();
            tasks = [.. list.Tasks.Where(t => owner.OwnsPath(t.Command) || owner.Mentions(t.Arguments))];
            if (list.Error != 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"Task Scheduler unavailable (0x{list.Error:X8})."));
            }
            else if (list.UnreadableItems > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{list.UnreadableItems} scheduled task folders or tasks could not be read."));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ShortcutTrace> shortcuts = anyRoot || productCode is not null
            ? [.. system.Shortcuts().Where(s =>
                (productCode is not null && s.MsiProductCode == productCode)
                || owner.OwnsPath(s.Target)
                || owner.Mentions(s.Arguments))]
            : [];

        var fingerprint = new Fingerprint
        {
            AppId = app.Id,
            TakenAt = _time.GetUtcNow(),
            Elapsed = Stopwatch.GetElapsedTime(start),
            Roots = roots,
            Files = files,
            ExecutableNames = executables,
            MsiProductCode = productCode,
            MsiUpgradeCode = productCode is { } code ? system.FindUpgradeCode(code, app.Scope) : null,
            InnoAppId = InnoAppId(app),
            Services = services,
            RunEntries = runEntries,
            AppPaths = appPaths,
            ScheduledTasks = tasks,
            Shortcuts = shortcuts,
            Notes = notes,
        };

        ScannerLog.FingerprintTaken(
            logger,
            app.Id,
            files?.Files ?? 0,
            services.Count + runEntries.Count + appPaths.Count + tasks.Count + shortcuts.Count,
            (long)fingerprint.Elapsed.TotalMilliseconds);
        return fingerprint;
    }

    internal static string? InnoAppId(InstalledApp app) =>
        app.InstallerKind == InstallerKind.InnoSetup && app.KeyName is { Length: > 4 } key && key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)
            ? key[..^4]
            : null;

    private (IReadOnlyList<string> Roots, string? WalkRoot) Roots(InstalledApp app, List<string> notes)
    {
        if (app.InstallLocation is null)
        {
            notes.Add("No install folder is known.");
            return ([], null);
        }

        PathDecision decision = guard.Check(app.InstallLocation);
        if (!decision.IsAllowed)
        {
            notes.Add($"Install folder {app.InstallLocation} is not used: {decision.Verdict}.");
            return ([], null);
        }

        if (app.InstallLocationIsEstimate)
        {
            notes.Add($"Install folder is an estimate ({app.InstallLocationOrigin}).");
        }

        IReadOnlyList<string> roots = decision.AllowedForms;
        if (!decision.Exists)
        {
            notes.Add("Install folder does not exist.");
            return (roots, null);
        }

        return (roots, roots[^1]);
    }
}
