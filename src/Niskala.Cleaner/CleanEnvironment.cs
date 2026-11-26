using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Cleaner.Rules;
using Niskala.Core.Safety;
using Niskala.Interop.Processes;

namespace Niskala.Cleaner;

public interface ICleanEnvironment
{
    bool IsElevated { get; }

    DateTimeOffset Now { get; }

    string? Resolve(CleanRoot root);

    IReadOnlySet<string> RunningProcessNames();
}

public sealed class SystemCleanEnvironment : ICleanEnvironment
{
    public static SystemCleanEnvironment Instance { get; } = new();

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public string? Resolve(CleanRoot root)
    {
        string path = root switch
        {
            CleanRoot.UserTemp => Path.GetTempPath(),
            CleanRoot.WindowsTemp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            CleanRoot.Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            CleanRoot.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CleanRoot.RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        return path.Length == 0 ? null : Path.TrimEndingDirectorySeparator(path);
    }

    public IReadOnlySet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ProcessNative.TrySnapshot(out List<ProcessEntry> processes) == ProcessNative.ErrorSuccess)
        {
            foreach (ProcessEntry process in processes)
            {
                names.Add(Path.GetFileNameWithoutExtension(process.ExeName));
            }
        }

        return names;
    }
}

public static class CleanerGuard
{
    private static readonly (CleanRoot Root, string Path, string Reason)[] CacheFolders =
    [
        (CleanRoot.Windows, "Temp", "Windows temporary files"),
        (CleanRoot.Windows, "Minidump", "kernel crash dumps"),
        (CleanRoot.Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", "Delivery Optimization cache"),
        (CleanRoot.ProgramData, @"Microsoft\Windows\WER\ReportArchive", "archived error reports"),
        (CleanRoot.ProgramData, @"Microsoft\Windows\WER\ReportQueue", "queued error reports"),
        (CleanRoot.ProgramData, @"Microsoft\Windows\WER\Temp", "error report scratch files"),
        (CleanRoot.LocalAppData, @"Microsoft\Windows\Explorer", "thumbnail and icon caches"),
        (CleanRoot.LocalAppData, @"Microsoft\Windows\INetCache", "shell web cache"),
        (CleanRoot.LocalAppData, @"Microsoft\Windows\WER\ReportArchive", "archived error reports"),
        (CleanRoot.LocalAppData, @"Microsoft\Windows\WER\ReportQueue", "queued error reports"),
    ];

    public static IEnumerable<ProtectedLocation> CacheCarveOuts(ICleanEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach ((CleanRoot root, string path, string reason) in CacheFolders)
        {
            if (environment.Resolve(root) is { } folder)
            {
                yield return new ProtectedLocation(Path.Combine(folder, path), ProtectionKind.Self, reason + " (cleaned by rule)");
            }
        }
    }

    public static PathGuard Create(MachineLayout layout, ICleanEnvironment environment, IPathResolver resolver, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        loggerFactory ??= NullLoggerFactory.Instance;
        ProtectedLocations locations = ProtectedLocations.Create(
            DefaultProtectedLocations.Build(layout).Concat(CacheCarveOuts(environment)),
            resolver,
            loggerFactory.CreateLogger<ProtectedLocations>());
        return new PathGuard(locations, resolver, loggerFactory.CreateLogger<PathGuard>());
    }

    public static PathGuard ForCurrentMachine(ILoggerFactory? loggerFactory = null) =>
        Create(MachineLayout.Detect(loggerFactory?.CreateLogger<MachineLayout>()), SystemCleanEnvironment.Instance, Win32PathResolver.Instance, loggerFactory);
}
