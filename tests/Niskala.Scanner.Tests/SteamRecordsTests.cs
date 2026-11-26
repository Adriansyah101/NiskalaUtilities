using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Scanner.Steam;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class SteamRecordsTests : IDisposable
{
    private const int AppId = 4000;
    private readonly ScratchFolder _scratch = new();
    private readonly string _keys = $@"{TestRoots.Key}\{Guid.NewGuid():N}";
    private readonly string _client;
    private readonly string _library;
    private readonly SteamLocations _locations;

    public SteamRecordsTests()
    {
        _client = Path.Combine(_scratch.Root, "Steam");
        _library = Path.Combine(_scratch.Root, "Library");
        Directory.CreateDirectory(Path.Combine(_library, "steamapps"));
        Directory.CreateDirectory(Path.Combine(_client, "steamapps"));
        File.WriteAllText(
            Path.Combine(_client, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escaped(_client)}\" }} \"1\" {{ \"path\" \"{Escaped(_library)}\" }} }}");

        string hkcu = $@"HKCU\{_keys}";
        _locations = new SteamLocations(_client)
        {
            AppsKey = $@"{hkcu}\Apps",
            UninstallKeys = [$@"{hkcu}\Uninstall64", $@"{hkcu}\Uninstall32"],
            TilePropertiesKey = $@"{hkcu}\Tiles",
            ShortcutFolders = [Path.Combine(_scratch.Root, "Desktop"), Path.Combine(_scratch.Root, "Start")],
        };
    }

    [Fact]
    public void Lists_what_Steam_keeps_under_the_AppId_and_nothing_of_other_apps()
    {
        string downloading = Folder(@"Library\steamapps\downloading\4000");
        string staging = Folder(@"Library\steamapps\temp\4000");
        string shaders = Folder(@"Steam\steamapps\shadercache\4000");
        string workshop = Folder(@"Library\steamapps\workshop\content\4000");
        string workshopManifest = _scratch.File(@"Library\steamapps\workshop\appworkshop_4000.acf", 8);
        string art = Folder(@"Steam\appcache\librarycache\4000");
        string header = _scratch.File(@"Steam\appcache\librarycache\4000_header.jpg", 8);
        _scratch.File(@"Steam\appcache\librarycache\40000_header.jpg", 8);
        _scratch.File(@"Steam\appcache\librarycache\400_header.jpg", 8);
        string saves = Folder(@"Steam\userdata\12345\4000");
        Folder(@"Steam\userdata\12345\40000");
        Folder(@"Steam\userdata\anonymous\4000");
        Folder(@"Steam\userdata\67890\7");
        string icon = _scratch.File(@"Steam\steam\games\a1.ico", 8);
        _scratch.File(@"Steam\steam\games\shared.ico", 8);
        string desktop = Shortcut(@"Desktop\Garry's Mod.url", "steam://rungameid/4000", Path.Combine(_client, @"steam\games\a1.ico"));
        string start = Shortcut(@"Start\Steam\Garry's Mod.url", "steam://rungameid/4000", Path.Combine(_client, @"steam\games\shared.ico"));
        Shortcut(@"Start\Steam\Other.url", "steam://rungameid/40000", Path.Combine(_client, @"steam\games\shared.ico"));
        string apps = Key(@"Apps\4000");
        Key(@"Apps\40000");
        string uninstall64 = Key(@"Uninstall64\Steam App 4000");
        string uninstall32 = Key(@"Uninstall32\Steam App 4000");
        string tile = Key(@"Tiles\W~steam://rungameid/4000");

        IReadOnlyList<SteamRecord> records = SteamRecords.Find(AppId, _locations);

        Assert.Equal(
            new HashSet<(LeftoverKind, string, bool)>
            {
                (LeftoverKind.Folder, shaders, false),
                (LeftoverKind.Folder, downloading, false),
                (LeftoverKind.Folder, staging, false),
                (LeftoverKind.Folder, workshop, false),
                (LeftoverKind.File, workshopManifest, false),
                (LeftoverKind.Folder, art, false),
                (LeftoverKind.File, header, false),
                (LeftoverKind.Folder, saves, true),
                (LeftoverKind.RegistryKey, apps, false),
                (LeftoverKind.RegistryKey, uninstall64, false),
                (LeftoverKind.RegistryKey, uninstall32, false),
                (LeftoverKind.File, desktop, false),
                (LeftoverKind.File, start, false),
                (LeftoverKind.File, icon, false),
                (LeftoverKind.RegistryKey, tile, false),
            },
            records.Select(r => (r.Kind, r.Path, r.PersonalData)).ToHashSet());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(760)]
    public void Steam_s_own_apps_have_no_records(int appId)
    {
        Folder($@"Steam\userdata\12345\{appId}");
        Folder($@"Steam\appcache\librarycache\{appId}");

        Assert.Empty(SteamRecords.Find(appId, _locations));
    }

    [Fact]
    public void After_the_removal_the_scan_proposes_Steam_records_and_keeps_saves_for_review()
    {
        string profile = Folder("Local");
        string savedGames = Folder("Saved Games");
        string cache = Folder(@"Steam\appcache\librarycache\4000");
        string cloud = Folder(@"Steam\userdata\12345\4000");
        string local = Folder(@"Local\GarrysMod");
        string saved = Folder(@"Saved Games\Garry's Mod");
        string apps = Key(@"Apps\4000");
        InstalledApp steamClient = App("reg:hklm32:Steam", "Steam") with { InstallLocation = _client, InstallLocationGuard = PathVerdict.Allowed };
        InstalledApp game = App("reg:hklm64:Steam App 4000", "Garry's Mod") with
        {
            KeyName = "Steam App 4000",
            InstallerKind = InstallerKind.Steam,
            InstallLocation = Path.Combine(_library, @"steamapps\common\GarrysMod"),
            InstallLocationGuard = PathVerdict.Allowed,
        };
        var fingerprint = new Fingerprint { AppId = game.Id, TakenAt = DateTimeOffset.UtcNow, Roots = [game.InstallLocation], ExecutableNames = ["hl2.exe"] };
        var trace = new FakeTraceSource();
        var scanner = new LeftoverScanner(
            ZGuard(),
            RegistryGuard.ForCurrentMachine(),
            new NoTrust(),
            new FileLeftoverProbe([new FileSearchRoot(profile, Kind: FileRootKind.Profile), new FileSearchRoot(savedGames, Kind: FileRootKind.Saves)], trace),
            new RegistryLeftoverProbe(trace, NullLogger<RegistryLeftoverProbe>.Instance),
            new FirewallLeftoverProbe(new NoFirewallRules()),
            new SteamLeftoverProbe(() => _locations),
            new ScheduledTaskLeftoverProbe(trace),
            new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
            SystemCommandEnvironment.Instance,
            NullLogger<LeftoverScanner>.Instance);

        LeftoverReport report = scanner.Scan(game, fingerprint, [steamClient], TestContext.Current.CancellationToken);

        Assert.Equal((90, ConfidenceTier.Safe), Scored(report, cache));
        Assert.Equal((90, ConfidenceTier.Safe), Scored(report, apps));
        Assert.Equal((79, ConfidenceTier.Review), Scored(report, cloud));
        Assert.Equal(ConfidenceTier.Review, Scored(report, local).Tier);
        Assert.Equal(ConfidenceTier.Review, Scored(report, saved).Tier);
        Assert.Contains(Item(report, saved).Reasons, r => r.Signal == LeftoverSignal.PersonalData);
        Assert.DoesNotContain(report.Excluded, e => e.Path.StartsWith(_client, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keys, throwOnMissingSubKey: false);
        _scratch.Dispose();
    }

    private static string Escaped(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static LeftoverItem Item(LeftoverReport report, string path) =>
        Assert.Single(report.Items, i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static (int Score, ConfidenceTier Tier) Scored(LeftoverReport report, string path)
    {
        LeftoverItem item = Item(report, path);
        return (item.Score, item.Tier);
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_scratch.Root, relative)).FullName;

    private string Shortcut(string relative, string url, string icon)
    {
        string path = Path.Combine(_scratch.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, ["[InternetShortcut]", "IDList=", "IconIndex=0", $"URL={url}", $"IconFile={icon}"]);
        return path;
    }

    private string Key(string relative)
    {
        Registry.CurrentUser.CreateSubKey($@"{_keys}\{relative}").Dispose();
        return $@"HKCU\{_keys}\{relative}";
    }

    private sealed class NoTrust : IFileTrustProbe
    {
        public ExclusionReason? Check(string path, bool isDirectory) => null;
    }

    private sealed class NoFirewallRules : IFirewallRuleSource
    {
        public int TryList(out List<Niskala.Interop.Firewall.FirewallRule> rules)
        {
            rules = [];
            return 0;
        }
    }
}
