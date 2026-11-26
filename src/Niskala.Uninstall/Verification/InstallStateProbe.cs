using System.IO.Enumeration;
using Microsoft.Win32;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Msi;
using Windows.Management.Deployment;

namespace Niskala.Uninstall.Verification;

public sealed record FolderState(bool Exists, int? Files, bool Truncated);

public interface IInstallStateProbe
{
    bool? KeyExists(string registryKey);

    bool? ProductRegistered(Guid productCode);

    bool? PackageExists(string packageFullName);

    FolderState Folder(string path);
}

public sealed class SystemInstallStateProbe : IInstallStateProbe
{
    private const int MaxCountedFiles = 100_000;

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static SystemInstallStateProbe Instance { get; } = new();

    public bool? KeyExists(string registryKey)
    {
        try
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(registryKey);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool? ProductRegistered(Guid productCode) =>
        MsiNative.QueryProductState(productCode.ToString("B")) switch
        {
            MsiProductState.NotRegistered => false,
            MsiProductState.Other => null,
            _ => true,
        };

    public bool? PackageExists(string packageFullName)
    {
        try
        {
            return new PackageManager().FindPackageForUser(string.Empty, packageFullName) is not null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public FolderState Folder(string path)
    {
        if (!Directory.Exists(path))
        {
            return new FolderState(false, null, false);
        }

        var files = new FileSystemEnumerable<bool>(path, static (ref FileSystemEntry _) => true, WalkOptions)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        int count = files.Take(MaxCountedFiles + 1).Count();
        return new FolderState(true, Math.Min(count, MaxCountedFiles), count > MaxCountedFiles);
    }
}
