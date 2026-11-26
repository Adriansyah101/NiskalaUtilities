using System.Security.AccessControl;
using System.Security.Principal;
using Niskala.Interop.Security;

namespace Niskala.Scanner.Leftovers;

public interface IFileTrustProbe
{
    ExclusionReason? Check(string path, bool isDirectory);
}

public sealed class SystemFileTrustProbe : IFileTrustProbe
{
    public const int MaxFolderSamples = 16;

    private static readonly string[] ProgramExtensions = [".exe", ".dll", ".sys", ".ocx", ".cpl", ".scr", ".msi"];

    public static SystemFileTrustProbe Instance { get; } = new();

    public static SecurityIdentifier TrustedInstaller { get; } = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public ExclusionReason? Check(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (IsOwnedByTrustedInstaller(path, isDirectory))
        {
            return ExclusionReason.TrustedInstallerOwned;
        }

        if (!isDirectory)
        {
            return IsProgram(path) && WinTrustNative.CheckFile(path) == SignatureCheck.MicrosoftSigned ? ExclusionReason.MicrosoftSigned : null;
        }

        string[] programs;
        try
        {
            programs = [.. Directory.EnumerateFiles(path).Where(IsProgram).Take(MaxFolderSamples)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return programs.Length > 0 && programs.All(p => WinTrustNative.CheckFile(p) == SignatureCheck.MicrosoftSigned)
            ? ExclusionReason.MicrosoftSigned
            : null;
    }

    public static bool IsProgram(string path) => ProgramExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsOwnedByTrustedInstaller(string path, bool isDirectory)
    {
        try
        {
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return TrustedInstaller.Equals(security.GetOwner(typeof(SecurityIdentifier)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            return false;
        }
    }
}
