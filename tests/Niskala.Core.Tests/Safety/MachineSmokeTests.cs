using Niskala.Core.Safety;

namespace Niskala.Core.Tests.Safety;

public sealed class MachineSmokeTests
{
    private static readonly PathGuard Guard = PathGuard.ForCurrentMachine();

    [Fact]
    public void Windows_and_its_contents_are_protected()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(PathVerdict.ProtectedTree, Guard.Check(windows).Verdict);
        Assert.Equal(PathVerdict.ProtectedTree, Guard.Check(Path.Combine(windows, "System32", "kernel32.dll")).Verdict);
    }

    [Fact]
    public void Program_Files_root_is_protected_but_a_vendor_folder_is_not()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(PathVerdict.ProtectedSelf, Guard.Check(programFiles).Verdict);

        PathDecision vendor = Guard.Check(Path.Combine(programFiles, "Niskala Test Vendor That Does Not Exist"));
        Assert.True(vendor.IsAllowed, vendor.ToString());
        Assert.False(vendor.Exists);
    }

    [Fact]
    public void Short_alias_of_Program_Files_is_still_protected()
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        string alias = Path.Combine(systemDrive, "PROGRA~1");
        if (!Directory.Exists(alias))
        {
            Assert.Skip("8.3 names are disabled on the system drive.");
        }

        Assert.Equal(PathVerdict.ProtectedSelf, Guard.Check(alias).Verdict);
    }

    [Fact]
    public void Current_profile_and_AppData_roots_are_protected()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(PathVerdict.ProtectedSelf, Guard.Check(profile).Verdict);
        Assert.Equal(PathVerdict.ProtectedSelf, Guard.Check(roaming).Verdict);
        Assert.Equal(PathVerdict.ProtectedSelf, Guard.Check(local).Verdict);
        Assert.Equal(PathVerdict.ProtectedTree, Guard.Check(Path.Combine(profile, "NTUSER.DAT")).Verdict);
        Assert.True(Guard.Check(Path.Combine(roaming, "Niskala Test Vendor")).IsAllowed);
    }

    [Fact]
    public void System_drive_root_is_denied()
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(PathVerdict.DriveRoot, Guard.Check(systemDrive).Verdict);
    }
}
