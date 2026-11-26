using System.Collections.Concurrent;
using System.Diagnostics;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Interop.Security;
using Niskala.Interop.Services;
using Niskala.Scanner.Sizing;

namespace Niskala.Optimizer.Resources;

public sealed record ProgramInfo(string Path, bool Exists, long Bytes, string? Publisher, bool IsMicrosoft, DiskKind Disk);

public interface IProgramFacts
{
    ProgramInfo Describe(string path);
}

public sealed class SystemProgramFacts(DiskKindProbe disks, PathGuard guard) : IProgramFacts
{
    private readonly ConcurrentDictionary<string, ProgramInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public ProgramInfo Describe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return _cache.GetOrAdd(path, Read);
    }

    private ProgramInfo Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new ProgramInfo(path, false, 0, null, false, DiskKind.Unknown);
        }

        bool inWindows = PathSyntax.IsSameOrUnder(path, _windows, out _);
        string? publisher = null;
        try
        {
            publisher = FileVersionInfo.GetVersionInfo(path).CompanyName is { Length: > 0 } company ? company.Trim() : null;
        }
        catch (FileNotFoundException)
        {
            publisher = null;
        }

        bool microsoft = inWindows
            || guard.Check(path).Verdict == PathVerdict.ProtectedTree
            || WinTrustNative.CheckFile(path) == SignatureCheck.MicrosoftSigned;
        return new ProgramInfo(path, true, file.Length, publisher, microsoft, disks.Probe(path).Kind);
    }
}

public static class BootImpactEstimator
{
    private const long Small = 1L << 20;
    private const long Medium = 10L << 20;
    private const long Large = 50L << 20;

    public static BootImpact Estimate(ResourceKind kind, bool enabled, ProgramInfo? program, ServiceStartType? start = null, bool delayed = false, bool atSignIn = true)
    {
        if (!enabled || kind == ResourceKind.ContextMenu
            || (kind == ResourceKind.Service && start != ServiceStartType.Automatic)
            || (kind == ResourceKind.ScheduledTask && !atSignIn))
        {
            return BootImpact.None;
        }

        if (program is not { Exists: true })
        {
            return BootImpact.Unknown;
        }

        int score = program.Bytes switch
        {
            < Small => 0,
            < Medium => 1,
            < Large => 2,
            _ => 3,
        };
        score += program.Disk == DiskKind.Hdd ? 1 : 0;
        score += kind == ResourceKind.Service ? (delayed ? -1 : 1) : 0;
        BootImpact impact = score switch
        {
            <= 1 => BootImpact.Low,
            2 => BootImpact.Medium,
            _ => BootImpact.High,
        };
        return kind == ResourceKind.RunOnceEntry && impact > BootImpact.Low ? BootImpact.Low : impact;
    }
}
