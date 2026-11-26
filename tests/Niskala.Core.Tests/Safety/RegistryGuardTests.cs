using Microsoft.Win32;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;

namespace Niskala.Core.Tests.Safety;

public sealed class RegistryGuardTests
{
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakeServices : IServiceFacts
    {
        private readonly Dictionary<string, ServiceFacts> _services = new(StringComparer.OrdinalIgnoreCase);

        public FakeServices Add(string name, int? start, string? imagePath, bool readable = true)
        {
            _services[name] = new ServiceFacts(name, start, imagePath, readable);
            return this;
        }

        public ServiceFacts? TryGet(string serviceName) => _services.GetValueOrDefault(serviceName);
    }

    private static readonly RegistryGuard Guard = new(
        RegistryRuleSet.Default,
        new FakeServices()
            .Add("AcmeSvc", 2, "\"C:\\Program Files\\Acme\\acmesvc.exe\" --service")
            .Add("AcmeUnquoted", 3, @"C:\Program Files\Acme\bin\acme helper.exe -k")
            .Add("Tcpip", 1, @"System32\drivers\tcpip.sys")
            .Add("Spooler", 2, @"%SystemRoot%\System32\spoolsv.exe")
            .Add("wuauserv", 3, @"%systemroot%\system32\svchost.exe -k netsvcs -p")
            .Add("AcmeDriver", 3, @"\SystemRoot\System32\drivers\acme.sys")
            .Add("NoImage", 3, null)
            .Add("Locked", null, null, readable: false),
        @"C:\Windows");

    [Theory]
    [InlineData(@"HKLM", RegistryVerdict.HiveRoot)]
    [InlineData(@"HKEY_CURRENT_USER\", RegistryVerdict.HiveRoot)]
    [InlineData(@"HKU", RegistryVerdict.HiveRoot)]
    [InlineData(@"HKCR\CLSID\{00000000-0000-0000-0000-000000000001}", RegistryVerdict.MergedClassesView)]
    [InlineData(@"HKEY_CURRENT_CONFIG\Software", RegistryVerdict.VirtualHive)]
    [InlineData(@"HKU\.DEFAULT\Software\Acme", RegistryVerdict.SystemAccountHive)]
    [InlineData(@"HKU\S-1-5-18\Software\Acme", RegistryVerdict.SystemAccountHive)]
    [InlineData(@"HKU\S-1-5-20\Software\Acme", RegistryVerdict.SystemAccountHive)]
    [InlineData(@"HKU\NotASid\Software\Acme", RegistryVerdict.UnknownHive)]
    [InlineData(@"HKXX\Software\Acme", RegistryVerdict.UnknownHive)]
    [InlineData(@"HKLM\SOFTWARE\\Acme", RegistryVerdict.InvalidSyntax)]
    [InlineData("HKLM\\SOFTWARE\\Acme\0Hidden", RegistryVerdict.InvalidSyntax)]
    [InlineData("", RegistryVerdict.InvalidSyntax)]
    [InlineData(@"HKLM\SOFTWARE", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Policies\Acme", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Office", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Clients\StartMenuInternet", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SYSTEM", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\AcmeSvc", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SAM\SAM\Domains", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\BCD00000000", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SECURITY\Policy\Secrets", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\HARDWARE\DESCRIPTION", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\Setup", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\CLSID", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\WOW6432Node\CLSID", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt\OpenWithProgids", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\exefile\shell\open", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes\exefile\DefaultIcon", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes\*", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\Background\shell", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKLM\SOFTWARE\Classes\AppX1234abcd", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes\Local Settings\Software", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes\SystemFileAssociations\.jpg", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKCU\Software", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKCU\Software\Microsoft\Office", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Start\TileProperties", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKCU\Control Panel\Desktop", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKCU\Environment", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKCU\Software\Classes\Local Settings\MuiCache", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKCU\Software\WOW6432Node\Microsoft\x", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKCU\AppEvents\Schemes\Apps\.Default", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKU\" + UserSid + @"\Software", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKU\" + UserSid + @"_Classes\CLSID", RegistryVerdict.ProtectedSelf)]
    [InlineData(@"HKU\" + UserSid, RegistryVerdict.HiveRoot)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip", RegistryVerdict.ServiceBootOrSystemStart)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler", RegistryVerdict.ServiceImageInWindows)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\wuauserv\Parameters", RegistryVerdict.ServiceImageInWindows)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeDriver", RegistryVerdict.ServiceImageInWindows)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\NoImage", RegistryVerdict.ServiceUnverifiable)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Locked", RegistryVerdict.ServiceUnverifiable)]
    public void Denies_protected_keys(string key, RegistryVerdict expected)
    {
        Assert.Equal(expected, Guard.CheckKey(key).Verdict);
    }

    [Theory]
    [InlineData(@"HKLM\SAM\SAM", "security account database")]
    [InlineData(@"HKLM\SECURITY\Policy", "local security authority secrets")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control", "active control set")]
    [InlineData(@"HKLM\SYSTEM\MountedDevices", "system configuration hive")]
    [InlineData(@"HKLM\BCD00000000\Objects", "boot configuration data")]
    public void Names_the_system_hive_that_blocks_a_key(string key, string reason)
    {
        RegistryDecision decision = Guard.CheckKey(key);

        Assert.Equal(RegistryVerdict.ProtectedTree, decision.Verdict);
        Assert.Contains(reason, decision.MatchedRule, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Acme")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Acme\Editor")]
    [InlineData(@"Computer\HKEY_LOCAL_MACHINE\SOFTWARE\Acme")]
    [InlineData(@"HKLM\SOFTWARE\Acme\")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Acme")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Acme_is1")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\acme.exe")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\0FEDCBA987654321")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Tracing\acme_RASAPI32")]
    [InlineData(@"HKLM\SOFTWARE\Clients\StartMenuInternet\AcmeBrowser")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Acme.Document")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.acmeproj")]
    [InlineData(@"HKLM\SOFTWARE\Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKLM\SOFTWARE\Classes\WOW6432Node\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKLM\SOFTWARE\Classes\exefile\shell\AcmeScan")]
    [InlineData(@"HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\AcmeExt")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\Background\shell\AcmeHere")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt\OpenWithProgids\Acme.Document")]
    [InlineData(@"HKLM\SOFTWARE\Classes\SystemFileAssociations\.jpg\shell\AcmeEdit")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Applications\acme.exe")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeUnquoted\Parameters")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\NotInstalledAnymore")]
    [InlineData(@"HKCU\Software\Acme")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Acme")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.acmeproj")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Start\TileProperties\W~steam://rungameid/4000")]
    [InlineData(@"HKCU\Software\Valve\Steam\Apps\4000")]
    [InlineData(@"HKCU\Software\Classes\Acme.Document")]
    [InlineData(@"HKCU\AppEvents\Schemes\Apps\Acme")]
    [InlineData(@"HKU\" + UserSid + @"\Software\Acme")]
    [InlineData(@"HKU\" + UserSid + @"_Classes\CLSID\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"HKU\S-1-12-1-1111111111-2222222222-3333333333-4444444444\Software\Acme")]
    public void Allows_application_keys(string key)
    {
        RegistryDecision decision = Guard.CheckKey(key);
        Assert.True(decision.IsAllowed, decision.ToString());
    }

    [Fact]
    public void WOW6432Node_is_folded_into_the_32_bit_view()
    {
        RegistryDecision decision = Guard.CheckKey(@"HKLM\SOFTWARE\WOW6432Node\Acme\Editor");

        Assert.True(decision.IsAllowed);
        Assert.Equal(RegistryView.Registry32, decision.Path!.View);
        Assert.Equal(@"SOFTWARE\Acme\Editor", decision.Path.PhysicalSubKey);
        Assert.Equal(@"HKLM\SOFTWARE\Acme\Editor", decision.Path.LogicalPath);
    }

    [Fact]
    public void User_classes_hive_maps_to_Software_Classes()
    {
        RegistryDecision decision = Guard.CheckKey($@"HKU\{UserSid}_Classes\Acme.Document");

        Assert.True(decision.IsAllowed);
        Assert.Equal(RegistryHive.Users, decision.Path!.Hive);
        Assert.Equal($@"{UserSid}_Classes\Acme.Document", decision.Path.PhysicalSubKey);
        Assert.Equal(["Software", "Classes", "Acme.Document"], decision.Path.Segments);
    }

    [Theory]
    [InlineData(@"HKCU\Environment", "Path", RegistryVerdict.ProtectedValue)]
    [InlineData(@"HKCU\Environment", "TEMP", RegistryVerdict.ProtectedValue)]
    [InlineData(@"HKCU\Environment", "", RegistryVerdict.ProtectedDefaultValue)]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt", "", RegistryVerdict.ProtectedDefaultValue)]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell", "Acme", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt", null, RegistryVerdict.ProtectedDefaultValue)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "SecurityHealth", RegistryVerdict.ProtectedValue)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "", RegistryVerdict.ProtectedDefaultValue)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler", "Start", RegistryVerdict.ServiceImageInWindows)]
    public void Denies_protected_values(string key, string? value, RegistryVerdict expected)
    {
        Assert.Equal(expected, Guard.CheckValue(key, value).Verdict);
    }

    [Theory]
    [InlineData(@"HKCU\Environment", "ACME_HOME")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "AcmeTray")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "AcmeTray")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt\OpenWithProgids", "Acme.Document")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", @"C:\Program Files\Acme\acme.dll")]
    [InlineData(@"HKLM\SOFTWARE\Acme", "")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", @"C:\Program Files\Acme\acme.exe.FriendlyAppName")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc", "Description")]
    public void Allows_application_values(string key, string value)
    {
        RegistryDecision decision = Guard.CheckValue(key, value);
        Assert.True(decision.IsAllowed, decision.ToString());
    }

    [Theory]
    [InlineData(@"HKCU\Environment", "Path", RegistryValueKind.ExpandString, RegistryValueKind.String, RegistryVerdict.Allowed)]
    [InlineData(@"HKCU\Environment", "path", RegistryValueKind.String, null, RegistryVerdict.Allowed)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path", RegistryValueKind.ExpandString, RegistryValueKind.ExpandString, RegistryVerdict.Allowed)]
    [InlineData(@"HKCU\Environment", "Path", RegistryValueKind.DWord, RegistryValueKind.String, RegistryVerdict.RevertNotString)]
    [InlineData(@"HKCU\Environment", "Path", RegistryValueKind.String, RegistryValueKind.Binary, RegistryVerdict.RevertNotString)]
    [InlineData(@"HKCU\Environment", "TEMP", RegistryValueKind.ExpandString, RegistryValueKind.ExpandString, RegistryVerdict.ProtectedValue)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "PATHEXT", RegistryValueKind.String, RegistryValueKind.String, RegistryVerdict.ProtectedTree)]
    [InlineData(@"HKCU\Software\Acme", "Mode", RegistryValueKind.DWord, RegistryValueKind.Binary, RegistryVerdict.Allowed)]
    public void Search_paths_may_have_only_their_string_data_written_back(string key, string value, RegistryValueKind previous, RegistryValueKind? current, RegistryVerdict expected)
    {
        Assert.Equal(expected, Guard.CheckRevert(key, value, previous, current).Verdict);
        Assert.False(Guard.CheckValue(@"HKCU\Environment", "Path").IsAllowed);
        Assert.False(Guard.CheckKey(@"HKCU\Environment").IsAllowed);
    }

    [Fact]
    public void Parent_of_a_protected_key_is_denied()
    {
        var rules = new RegistryRuleSet(
            [
                new RegistryRule(RegistryRoot.Machine, [], ProtectionKind.Self, "root"),
                new RegistryRule(RegistryRoot.Machine, ["SOFTWARE", "Vendor", RegistryRule.AnyKey, "Keep"], ProtectionKind.Tree, "deep"),
            ],
            []);
        var guard = new RegistryGuard(rules, new FakeServices(), @"C:\Windows");

        Assert.Equal(RegistryVerdict.ContainsProtected, guard.CheckKey(@"HKLM\SOFTWARE\Vendor").Verdict);
        Assert.Equal(RegistryVerdict.ContainsProtected, guard.CheckKey(@"HKLM\SOFTWARE\Vendor\Product").Verdict);
        Assert.Equal(RegistryVerdict.ProtectedTree, guard.CheckKey(@"HKLM\SOFTWARE\Vendor\Product\Keep\x").Verdict);
        Assert.True(guard.CheckKey(@"HKLM\SOFTWARE\Vendor\Product\Other").IsAllowed);
        Assert.True(guard.CheckKey(@"HKLM\SOFTWARE\Elsewhere").IsAllowed);
    }

    [Fact]
    public void Prefix_tokens_match_key_name_starts()
    {
        var rules = new RegistryRuleSet(
            [new RegistryRule(RegistryRoot.User, ["Software", "{prefix:Acme}"], ProtectionKind.Tree, "prefix")],
            []);
        var guard = new RegistryGuard(rules, new FakeServices(), @"C:\Windows");

        Assert.Equal(RegistryVerdict.ProtectedTree, guard.CheckKey(@"HKCU\Software\AcmeTools\x").Verdict);
        Assert.True(guard.CheckKey(@"HKCU\Software\Other").IsAllowed);
    }
}
