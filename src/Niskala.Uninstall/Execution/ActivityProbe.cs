using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Registry;

namespace Niskala.Uninstall.Execution;

public interface IActivityProbe
{
    long Stamp();
}

public sealed class UninstallActivityProbe : IActivityProbe, IDisposable
{
    private readonly ILogger _logger;
    private readonly string? _key;
    private readonly string? _parentKey;
    private readonly List<FileSystemWatcher> _watchers = [];
    private long _folderChanges;

    public UninstallActivityProbe(string? registryKey, string? installRoot, ILogger<UninstallActivityProbe> logger)
    {
        _logger = logger;
        if (registryKey is not null && RegistryPath.TryParse(registryKey, RegistryView.Default, out _) == RegistryVerdict.Allowed)
        {
            _key = registryKey;
            _parentKey = registryKey[..registryKey.LastIndexOf('\\')];
        }

        if (installRoot is not null)
        {
            Watch(installRoot, filter: null, recursive: true);
            if (Path.GetDirectoryName(installRoot) is { } parent)
            {
                Watch(parent, Path.GetFileName(installRoot), recursive: false);
            }
        }
    }

    public long Stamp() => HashCode.Combine(Interlocked.Read(ref _folderChanges), KeyWriteTicks(_key), KeyWriteTicks(_parentKey));

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private static long KeyWriteTicks(string? registryKey)
    {
        if (registryKey is null)
        {
            return 0;
        }

        try
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(registryKey);
            return key is null ? -1
                : RegistryNative.TryGetLastWriteTime(key.Handle, out DateTime written) == 0 ? written.Ticks
                : -2;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return -3;
        }
    }

    private void Watch(string folder, string? filter, bool recursive)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                Filter = filter ?? string.Empty,
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Changed += (_, _) => Touch();
            watcher.Created += (_, _) => Touch();
            watcher.Deleted += (_, _) => Touch();
            watcher.Renamed += (_, _) => Touch();
            watcher.Error += (_, _) => Touch();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            UninstallLog.WatchFailed(_logger, folder, ex);
        }
    }

    private void Touch() => Interlocked.Increment(ref _folderChanges);
}
