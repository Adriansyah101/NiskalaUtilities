using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Inventory.Msi;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class InstallLocationEnricherTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ScratchFolder _scratch = new();
    private readonly PathGuard _guard = ZGuard(
        new ProtectedLocation(@"Z:\Windows", ProtectionKind.Tree, "windows"),
        new ProtectedLocation(@"Z:\Program Files", ProtectionKind.Self, "pf"));

    private sealed class CountingIndex(Dictionary<Guid, string[]> paths) : IMsiComponentPaths
    {
        public List<Guid> Requested { get; } = [];

        public IReadOnlyList<string> GetComponentPaths(Guid productCode)
        {
            Requested.Add(productCode);
            return paths.GetValueOrDefault(productCode, []);
        }
    }

    public void Dispose() => _scratch.Dispose();

    private static InstalledApp MsiApp(Guid code) => App($"reg:hklm64:{code:B}") with
    {
        InstallerKind = InstallerKind.Msi,
        MsiProductCode = code,
        KeyLastWriteUtc = Stamp,
    };

    private (InstallLocationEnricher Enricher, List<IReadOnlyCollection<Guid>> IndexRequests, MsiLocationCache Cache) Create(CountingIndex index)
    {
        var requests = new List<IReadOnlyCollection<Guid>>();
        MsiLocationCache cache = MsiLocationCache.Load(Path.Combine(_scratch.Root, "cache.json"), NullLogger<MsiLocationCache>.Instance);
        var enricher = new InstallLocationEnricher(
            _guard,
            products =>
            {
                requests.Add(products);
                return index;
            },
            cache,
            NullLogger<InstallLocationEnricher>.Instance);
        return (enricher, requests, cache);
    }

    [Fact]
    public void Records_the_guard_verdict_of_recorded_locations()
    {
        (InstallLocationEnricher enricher, List<IReadOnlyCollection<Guid>> requests, _) = Create(new CountingIndex([]));

        IReadOnlyList<InstalledApp> result = enricher.Enrich(
            [App("a") with { InstallLocation = @"Z:\Program Files" }, App("b") with { InstallLocation = @"Z:\Program Files\Acme" }],
            buildComponentIndex: true, TestContext.Current.CancellationToken);

        Assert.Equal(PathVerdict.ProtectedSelf, result[0].InstallLocationGuard);
        Assert.Equal(PathVerdict.Allowed, result[1].InstallLocationGuard);
        Assert.Empty(requests);
    }

    [Fact]
    public void Index_is_built_once_for_cache_misses_and_the_result_is_cached()
    {
        Guid code = Guid.NewGuid();
        var index = new CountingIndex(new() { [code] = [@"Z:\Program Files\Acme\a.exe", @"Z:\Program Files\Acme\bin\b.dll"] });
        (InstallLocationEnricher enricher, List<IReadOnlyCollection<Guid>> requests, MsiLocationCache cache) = Create(index);

        InstalledApp app = Assert.Single(enricher.Enrich([MsiApp(code)], buildComponentIndex: true, TestContext.Current.CancellationToken));

        Assert.Equal(@"Z:\Program Files\Acme", app.InstallLocation);
        Assert.Equal(InstallLocationOrigin.MsiComponents, app.InstallLocationOrigin);
        Assert.True(app.InstallLocationIsEstimate);
        Assert.Equal([code], Assert.Single(requests));
        Assert.True(cache.TryGet(code, Stamp, out string? cached));
        Assert.Equal(@"Z:\Program Files\Acme", cached);
    }

    [Fact]
    public void Cache_hit_never_builds_the_index()
    {
        Guid code = Guid.NewGuid();
        (InstallLocationEnricher enricher, List<IReadOnlyCollection<Guid>> requests, MsiLocationCache cache) = Create(new CountingIndex([]));
        cache.Set(code, Stamp, @"Z:\Acme");

        InstalledApp app = Assert.Single(enricher.Enrich([MsiApp(code)], buildComponentIndex: true, TestContext.Current.CancellationToken));

        Assert.Equal(@"Z:\Acme", app.InstallLocation);
        Assert.Empty(requests);
    }

    [Fact]
    public void Index_build_can_be_switched_off()
    {
        (InstallLocationEnricher enricher, List<IReadOnlyCollection<Guid>> requests, _) = Create(new CountingIndex([]));

        InstalledApp app = Assert.Single(enricher.Enrich([MsiApp(Guid.NewGuid())], buildComponentIndex: false, TestContext.Current.CancellationToken));

        Assert.Null(app.InstallLocation);
        Assert.Empty(requests);
    }

    [Fact]
    public void Uninstaller_folder_is_the_last_fallback_but_never_a_shared_one_or_the_Steam_client()
    {
        (InstallLocationEnricher enricher, _, _) = Create(new CountingIndex([]));
        ParsedCommand Command(string exe) => new(exe, exe, string.Empty, true, CommandParseIssue.None);

        IReadOnlyList<InstalledApp> result = enricher.Enrich(
            [
                App("own") with { InstallerKind = InstallerKind.Nsis, UninstallCommand = Command(@"Z:\Program Files\Acme\uninst.exe") },
                App("cache") with { InstallerKind = InstallerKind.WixBurn, UninstallCommand = Command(@"Z:\ProgramData\Package Cache\{X}\setup.exe") },
                App("temp") with { InstallerKind = InstallerKind.CustomExe, UninstallCommand = Command(@"Z:\Users\bob\AppData\Local\Temp\u.exe") },
                App("steam") with { InstallerKind = InstallerKind.Steam, UninstallCommand = Command(@"Z:\Program Files (x86)\Steam\steam.exe") },
            ],
            buildComponentIndex: true, TestContext.Current.CancellationToken);

        Assert.Equal(@"Z:\Program Files\Acme", result[0].InstallLocation);
        Assert.Equal(InstallLocationOrigin.UninstallerDirectory, result[0].InstallLocationOrigin);
        Assert.Null(result[1].InstallLocation);
        Assert.Null(result[2].InstallLocation);
        Assert.Null(result[3].InstallLocation);
    }
}
