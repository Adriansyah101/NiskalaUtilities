using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Leftovers;
using Niskala.Scanner.Steam;
using Niskala.Uninstall.Steam;

namespace Niskala.Uninstall.Tests;

public sealed class SteamUninstallerTests : IDisposable
{
    private const int AppId = 4000;
    private readonly string _scratch = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _keys = $@"{TestRoots.Key}\{Guid.NewGuid():N}";
    private readonly string _client;
    private readonly string _apps;
    private readonly SteamLocations _locations;

    public SteamUninstallerTests()
    {
        _client = Path.Combine(_scratch, "Steam");
        _apps = Directory.CreateDirectory(Path.Combine(_client, "steamapps")).FullName;
        File.WriteAllText(Path.Combine(_apps, $"appmanifest_{AppId}.acf"), $"\"AppState\" {{ \"appid\" \"{AppId}\" \"name\" \"Garry's Mod\" \"installdir\" \"GarrysMod\" \"SizeOnDisk\" \"4096\" }}");
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(_apps, "common", "GarrysMod")).FullName, "hl2.exe"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(_apps, "shadercache", $"{AppId}"));
        Directory.CreateDirectory(Path.Combine(_apps, "common", "OtherGame"));
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(_client, "appcache", "librarycache")).FullName, $"{AppId}_header.jpg"), new byte[8]);
        Directory.CreateDirectory(Path.Combine(_client, "userdata", "12345", $"{AppId}", "remote"));
        Registry.CurrentUser.CreateSubKey($@"{_keys}\Apps\{AppId}").Dispose();
        _locations = new SteamLocations(_client)
        {
            AppsKey = $@"HKCU\{_keys}\Apps",
            UninstallKeys = [$@"HKCU\{_keys}\Uninstall"],
            TilePropertiesKey = $@"HKCU\{_keys}\Tiles",
            ShortcutFolders = [],
        };
    }

    [Fact]
    public void The_plan_lists_what_exists_and_the_uninstall_entry_but_not_the_Cloud_saves()
    {
        SteamGame game = SteamLibrary.Find(AppId, _client)!;

        IReadOnlyList<LeftoverItem> items = SteamUninstaller.Items(Game(), AppId, game, _locations);

        Assert.Equal(
            [
                (LeftoverKind.Folder, game.InstallFolder),
                (LeftoverKind.File, game.ManifestFile),
                (LeftoverKind.Folder, Path.Combine(_apps, "shadercache", $"{AppId}")),
                (LeftoverKind.File, Path.Combine(_client, "appcache", "librarycache", $"{AppId}_header.jpg")),
                (LeftoverKind.RegistryKey, $@"HKCU\{_keys}\Apps\{AppId}"),
                (LeftoverKind.RegistryKey, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 4000"),
            ],
            items.Select(i => (i.Kind, i.Path)));
        Assert.All(items, i => Assert.Equal(ConfidenceTier.Safe, i.Tier));
        Assert.Equal(4096, items[0].Bytes);
    }

    [Fact]
    public async Task A_game_whose_files_are_gone_gets_a_plan_of_Steam_s_records()
    {
        File.Delete(Path.Combine(_apps, $"appmanifest_{AppId}.acf"));
        Directory.Delete(Path.Combine(_apps, "common", "GarrysMod"), recursive: true);
        SteamUninstaller steam = Uninstaller(new FakeSteam(_client), out _);

        SteamPlan plan = steam.Plan(Game() with { InstallLocation = null }, out string? problem, TestContext.Current.CancellationToken)!;

        Assert.Null(problem);
        Assert.Null(plan.Game);
        Assert.Equal(
            [
                Path.Combine(_apps, "shadercache", $"{AppId}"),
                Path.Combine(_client, "appcache", "librarycache", $"{AppId}_header.jpg"),
                $@"HKCU\{_keys}\Apps\{AppId}",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 4000",
            ],
            plan.Items.Select(i => i.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => steam.ExecuteAsync(plan, new SteamOptions { Removal = SteamRemoval.ViaSteam }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_entry_that_names_another_folder_gets_no_plan()
    {
        SteamUninstaller steam = Uninstaller(new FakeSteam(_client), out _);

        SteamPlan? plan = steam.Plan(Game(Path.Combine(_apps, "common", "OtherGame")), out string? problem, TestContext.Current.CancellationToken);

        Assert.Null(plan);
        Assert.Contains("Steam's manifest names", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_direct_removal_waits_for_Steam_to_close_and_keeps_other_games()
    {
        var client = new FakeSteam(_client) { Running = true };
        SteamUninstaller steam = Uninstaller(client, out TransactionStore store);
        SteamPlan plan = steam.Plan(Game(), out _, TestContext.Current.CancellationToken)!;
        var options = new SteamOptions { Permanent = true, CreateRestorePoint = false };
        string shaders = Path.Combine(_apps, "shadercache", $"{AppId}");

        SteamResult refused = await steam.ExecuteAsync(plan, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SteamOutcome.SteamRunning, refused.Outcome);
        Assert.True(Directory.Exists(plan.Game!.InstallFolder));

        SteamResult result = await steam.ExecuteAsync(plan, options with { Removal = SteamRemoval.CloseSteam }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["shutdown"], client.Calls);
        Assert.Equal(SteamOutcome.Removed, result.Outcome);
        Assert.Equal(TransactionKind.Force, store.Load(result.Clean!.Transaction.Id)?.Kind);
        Assert.False(Directory.Exists(plan.Game!.InstallFolder));
        Assert.False(File.Exists(plan.Game!.ManifestFile));
        Assert.False(Directory.Exists(shaders));
        Assert.True(Directory.Exists(Path.Combine(_apps, "common", "OtherGame")));
    }

    [Fact]
    public async Task Via_Steam_removes_nothing_itself()
    {
        var client = new FakeSteam(_client) { Running = true };
        SteamUninstaller steam = Uninstaller(client, out _);
        SteamPlan plan = steam.Plan(Game(), out _, TestContext.Current.CancellationToken)!;

        SteamResult result = await steam.ExecuteAsync(plan, new SteamOptions { Removal = SteamRemoval.ViaSteam }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SteamOutcome.HandedToSteam, result.Outcome);
        Assert.Equal([$"uninstall {AppId}"], client.Calls);
        Assert.True(Directory.Exists(plan.Game!.InstallFolder));
    }

    [Fact]
    public async Task The_fingerprint_taken_before_a_direct_removal_finds_what_is_left_for_review()
    {
        SteamUninstaller steam = Uninstaller(new FakeSteam(_client), out _);
        SteamPlan plan = steam.Plan(Game(), out _, TestContext.Current.CancellationToken)!;
        string cloud = Path.Combine(_client, "userdata", "12345", $"{AppId}");

        SteamResult result = await steam.ExecuteAsync(plan, new SteamOptions { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);
        LeftoverReport report = Scanner().Scan(plan.App, plan.Fingerprint, [], TestContext.Current.CancellationToken);

        Assert.Equal(SteamOutcome.Removed, result.Outcome);
        Assert.Equal(["hl2.exe"], plan.Fingerprint.ExecutableNames);
        FingerprintRecord saved = Assert.Single(new FingerprintStore(Path.Combine(_scratch, "fingerprints"), NullLogger<FingerprintStore>.Instance).LoadAll());
        Assert.Equal(plan.App.Id, saved.App.Id);
        Assert.False(File.Exists(Path.Combine(_client, "appcache", "librarycache", $"{AppId}_header.jpg")));
        LeftoverItem left = Assert.Single(report.Items);
        Assert.Equal((cloud, ConfidenceTier.Review), (left.Path, left.Tier));
        Assert.True(Directory.Exists(cloud));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_keys, throwOnMissingSubKey: false);
        Directory.Delete(_scratch, recursive: true);
    }

    private InstalledApp Game(string? location = null) => new()
    {
        Id = $"reg:hklm64:Steam App {AppId}",
        Sources = AppSources.Registry,
        Scope = InstallScope.Machine,
        KeyName = $"Steam App {AppId}",
        RegistryKey = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {AppId}",
        DisplayName = "Garry's Mod",
        InstallerKind = InstallerKind.Steam,
        InstallLocation = location ?? Path.Combine(_apps, "common", "GarrysMod"),
        InstallLocationGuard = PathVerdict.Allowed,
    };

    private SteamUninstaller Uninstaller(FakeSteam client, out TransactionStore store)
    {
        var guard = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);
        store = new TransactionStore(Path.Combine(_scratch, "journal"));
        var cleaner = new LeftoverCleaner(
            guard,
            RegistryGuard.ForCurrentMachine(),
            store,
            new FakeRestorePoints(),
            new FakeFirewallEditor(),
            new FakeTaskEditor(),
            new FakeServices(),
            SystemCommandEnvironment.Instance,
            new EmptyParentFinder([_scratch], guard),
            NullLogger<LeftoverCleaner>.Instance);
        return new SteamUninstaller(
            new FingerprintCollector(guard, new EmptyTraceSource(), SystemCommandEnvironment.Instance, NullLogger<FingerprintCollector>.Instance),
            new FingerprintStore(Path.Combine(_scratch, "fingerprints"), NullLogger<FingerprintStore>.Instance),
            cleaner,
            client,
            NullLogger<SteamUninstaller>.Instance,
            _ => _locations);
    }

    private LeftoverScanner Scanner()
    {
        var guard = new PathGuard(ProtectedLocations.Create([], Win32PathResolver.Instance), Win32PathResolver.Instance);
        var trace = new EmptyTraceSource();
        return new LeftoverScanner(
            guard,
            RegistryGuard.ForCurrentMachine(),
            new NoTrust(),
            new FileLeftoverProbe([], trace),
            new RegistryLeftoverProbe(trace, NullLogger<RegistryLeftoverProbe>.Instance),
            new FirewallLeftoverProbe(new FakeFirewallEditor()),
            new SteamLeftoverProbe(() => _locations),
            new ScheduledTaskLeftoverProbe(trace),
            new PackageLeftoverProbe(Path.Combine(TestRoots.Folder, "no-packages")),
            SystemCommandEnvironment.Instance,
            NullLogger<LeftoverScanner>.Instance);
    }

    private sealed class FakeSteam(string folder) : ISteamClient
    {
        public bool Running { get; set; }

        public List<string> Calls { get; } = [];

        public string? ClientFolder() => folder;

        public bool IsRunning(string clientFolder) => Running;

        public int Shutdown(string clientFolder)
        {
            Calls.Add("shutdown");
            return 0;
        }

        public Task<bool> WaitForExitAsync(string clientFolder, TimeSpan wait, CancellationToken cancellationToken)
        {
            Running = false;
            return Task.FromResult(true);
        }

        public int OpenUninstall(string clientFolder, int appId)
        {
            Calls.Add($"uninstall {appId}");
            return 0;
        }
    }
}
