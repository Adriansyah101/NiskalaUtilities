using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niskala.Uninstall.Security;

public enum LocationTrust
{
    Trusted,
    UserWritable,
    Unknown,
}

public sealed record LocationCheck(LocationTrust Trust, string? Path = null, string? Principal = null);

public interface IWritableLocationProbe
{
    LocationCheck Check(string file);
}

public sealed class AclWritableLocationProbe(ILogger<AclWritableLocationProbe>? logger = null) : IWritableLocationProbe
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x1000_0000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x4000_0000;

    private const FileSystemRights FileDanger =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    private const FileSystemRights FolderDanger =
        FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    private static readonly SecurityIdentifier[] TrustedAccounts =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalServiceSid, null),
        new(WellKnownSidType.NetworkServiceSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
    ];

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public LocationCheck Check(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        try
        {
            var info = new FileInfo(file);
            if (Find(info.GetAccessControl(), file, FileDanger) is { } fileFinding)
            {
                return fileFinding;
            }

            string? folder = info.DirectoryName;
            return folder is not null && Find(new DirectoryInfo(folder).GetAccessControl(), folder, FolderDanger) is { } folderFinding
                ? folderFinding
                : new LocationCheck(LocationTrust.Trusted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            UninstallLog.AclUnreadable(_logger, file, ex);
            return new LocationCheck(LocationTrust.Unknown, file);
        }
    }

    private static LocationCheck? Find(FileSystemSecurity security, string path, FileSystemRights danger)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner))
        {
            return new LocationCheck(LocationTrust.UserWritable, path, Describe(owner));
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & danger) != 0
                && rule.IdentityReference is SecurityIdentifier sid
                && !IsTrusted(sid))
            {
                return new LocationCheck(LocationTrust.UserWritable, path, Describe(sid));
            }
        }

        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => Array.IndexOf(TrustedAccounts, sid) >= 0;

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
