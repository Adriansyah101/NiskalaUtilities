using Niskala.Core.Safety;

namespace Niskala.Core.Tests.Safety;

public sealed class DefaultProtectedLocationsTests
{
    private static readonly MachineLayout Layout = new()
    {
        WindowsDirectory = @"Z:\Windows",
        ProgramFilesDirectories = [@"Z:\Program Files", @"Z:\Program Files (x86)"],
        CommonProgramFilesDirectories = [@"Z:\Program Files\Common Files", @"Z:\Program Files (x86)\Common Files"],
        ProgramDataDirectory = @"Z:\ProgramData",
        ProfilesDirectory = @"Z:\Users",
        PublicProfile = @"Z:\Users\Public",
        DefaultProfile = @"Z:\Users\Default",
        UserProfiles = [@"Z:\Users\bob", @"Z:\Users\alice"],
        CurrentUserKnownFolders = [@"Z:\Users\bob\OneDrive\Documents"],
        DriveRoots = [@"Z:\"],
        ApplicationDirectories = [@"Z:\Users\bob\AppData\Local\Niskala"],
    };

    private static readonly PathGuard Guard =
        new(ProtectedLocations.FromLayout(Layout, new FakeResolver()), new FakeResolver());

    [Theory]
    [InlineData(@"Z:\Windows", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Windows\System32\config\SYSTEM", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Windows\Prefetch", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Windows\Prefetch\ACME.EXE-1A2B3C4D.pf", PathVerdict.Allowed)]
    [InlineData(@"Z:\Windows\Prefetch\ReadyBoot\Trace1.fx", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Windows\Prefetch\Layout.ini", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\pagefile.sys", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\System Volume Information", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\$Recycle.Bin\S-1-5-21-1\x", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Config.Msi", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\", PathVerdict.DriveRoot)]
    [InlineData(@"Z:\Program Files", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Program Files (x86)", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Program Files\WindowsApps\Some.Package_1.0", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Program Files\Windows Defender", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Windows\WinSxS\amd64_microsoft-windows-shell32", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Windows\System32\drivers", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Boot\BCD", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\EFI\Microsoft\Boot", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Program Files (x86)\Microsoft\EdgeWebView\Application", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Program Files (x86)\Microsoft\EdgeUpdate", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Program Files (x86)\Microsoft", PathVerdict.ContainsProtected)]
    [InlineData(@"Z:\Program Files (x86)\Microsoft\Acme Tool", PathVerdict.Allowed)]
    [InlineData(@"Z:\Program Files\Common Files", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Program Files\Common Files\microsoft shared\ink", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\ProgramData", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows Defender", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\ProgramData\Package Cache", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Administrative Tools", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\Default\AppData\Roaming\x", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users\Public", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\Public\Desktop", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\alice", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\NTUSER.DAT", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users\bob\AppData", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\AppData\Roaming", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\AppData\Local\Temp", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\AppData\Local\Microsoft\Windows\UsrClass.dat", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users\bob\AppData\Local\Microsoft\WindowsApps\winget.exe", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users\bob\AppData\Roaming\Microsoft\Windows\Recent", PathVerdict.ProtectedTree)]
    [InlineData(@"Z:\Users\bob\Documents", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\OneDrive\Documents", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\OneDrive", PathVerdict.ProtectedSelf)]
    [InlineData(@"Z:\Users\bob\AppData\Local\Niskala\backups\x.reg", PathVerdict.ProtectedTree)]
    public void Protects_system_and_user_locations(string path, PathVerdict expected)
    {
        Assert.Equal(expected, Guard.Check(path).Verdict);
    }

    [Theory]
    [InlineData(@"Z:\Program Files\Acme")]
    [InlineData(@"Z:\Program Files (x86)\Acme\Editor")]
    [InlineData(@"Z:\Program Files\Common Files\Acme Shared")]
    [InlineData(@"Z:\ProgramData\Acme")]
    [InlineData(@"Z:\ProgramData\Package Cache\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme")]
    [InlineData(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Acme.lnk")]
    [InlineData(@"Z:\Users\bob\AppData\Roaming\Acme")]
    [InlineData(@"Z:\Users\bob\AppData\Local\Acme")]
    [InlineData(@"Z:\Users\bob\AppData\LocalLow\Acme")]
    [InlineData(@"Z:\Users\bob\AppData\Local\Programs\acme-app")]
    [InlineData(@"Z:\Users\bob\AppData\Local\Temp\acme-setup.log")]
    [InlineData(@"Z:\Users\bob\AppData\Local\Microsoft\Edge")]
    [InlineData(@"Z:\Users\bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Acme")]
    [InlineData(@"Z:\Users\bob\Documents\Acme Projects")]
    [InlineData(@"Z:\Users\bob\Desktop\Acme.lnk")]
    [InlineData(@"Z:\Users\alice\AppData\Roaming\Acme")]
    [InlineData(@"Z:\Games\Acme")]
    public void Allows_application_locations(string path)
    {
        PathDecision decision = Guard.Check(path);
        Assert.True(decision.IsAllowed, decision.ToString());
    }
}
