using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Safety;
using Niskala.Scanner.Inventory.Msi;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class MsiTests
{
    private static readonly PathGuard Guard = ZGuard(
        new ProtectedLocation(@"Z:\Windows", ProtectionKind.Tree, "windows"),
        new ProtectedLocation(@"Z:\Program Files", ProtectionKind.Self, "pf"),
        new ProtectedLocation(@"Z:\Program Files\Common Files", ProtectionKind.Self, "cf"));

    [Theory]
    [InlineData("12345678-ABCD-EF01-2345-6789ABCDEF01", "87654321DCBA10FE32547698BADCFE10")]
    [InlineData("00000000-0000-0000-0000-000000000001", "00000000000000000000000000000010")]
    public void Packs_product_codes_like_Windows_Installer(string code, string packed)
    {
        Assert.Equal(packed, MsiPackedGuid.Pack(Guid.Parse(code)));
    }

    [Fact]
    public void Infers_the_common_ancestor_of_the_product_files()
    {
        string? location = MsiLocationInference.Infer(
            [@"Z:\Program Files\Acme\bin\acme.exe", @"Z:\Program Files\Acme\lib\core.dll", @"Z:\Program Files\Acme\readme.txt"],
            Guard);

        Assert.Equal(@"Z:\Program Files\Acme", location);
    }

    [Fact]
    public void Ignores_system_files_of_the_product()
    {
        Assert.Equal(@"Z:\Program Files\Acme", MsiLocationInference.Infer([@"Z:\Program Files\Acme\acme.exe", @"Z:\Windows\System32\acmehelper.dll"], Guard));
    }

    [Fact]
    public void Protected_common_ancestor_falls_back_to_the_busiest_folder()
    {
        string? location = MsiLocationInference.Infer(
            [@"Z:\Program Files\Acme\a.exe", @"Z:\Program Files\Acme\b.dll", @"Z:\Program Files\Common Files\Acme\shared.dll"],
            Guard);

        Assert.Equal(@"Z:\Program Files\Acme", location);
    }

    [Fact]
    public void Folder_key_paths_are_used_as_folders()
    {
        Assert.Equal(@"Z:\Acme\Data", MsiLocationInference.Infer([@"Z:\Acme\Data\"], Guard));
    }

    [Fact]
    public void Only_protected_folders_give_no_location()
    {
        Assert.Null(MsiLocationInference.Infer([@"Z:\Windows\System32\x.dll", @"Z:\Program Files\x.dll"], Guard));
    }

    [Theory]
    [InlineData(@"Z:\A\B\C", @"Z:\A\B\D", @"Z:\A\B")]
    [InlineData(@"Z:\A", @"Z:\B", @"Z:\")]
    [InlineData(@"Z:\A", @"Y:\A", "")]
    public void Common_ancestor(string a, string b, string expected)
    {
        Assert.Equal(expected, MsiLocationInference.CommonAncestor(a, b));
    }

    [Fact]
    public void Cache_round_trips_and_invalidates_on_a_new_stamp()
    {
        using var scratch = new ScratchFolder();
        string file = Path.Combine(scratch.Root, "cache", "msi.json");
        Guid code = Guid.NewGuid();
        Guid empty = Guid.NewGuid();
        var stamp = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        MsiLocationCache cache = MsiLocationCache.Load(file, NullLogger<MsiLocationCache>.Instance);
        cache.Set(code, stamp, @"C:\Program Files\Acme");
        cache.Set(empty, stamp, null);
        cache.Save();

        MsiLocationCache reloaded = MsiLocationCache.Load(file, NullLogger<MsiLocationCache>.Instance);
        Assert.True(reloaded.TryGet(code, stamp, out string? location));
        Assert.Equal(@"C:\Program Files\Acme", location);
        Assert.True(reloaded.TryGet(empty, stamp, out string? none));
        Assert.Null(none);
        Assert.False(reloaded.TryGet(code, stamp.AddSeconds(1), out _));
        Assert.False(reloaded.TryGet(Guid.NewGuid(), stamp, out _));
    }

    [Fact]
    public void Corrupt_cache_file_starts_empty()
    {
        using var scratch = new ScratchFolder();
        string file = scratch.File("msi.json", "{ not json"u8.ToArray());

        MsiLocationCache cache = MsiLocationCache.Load(file, NullLogger<MsiLocationCache>.Instance);

        Assert.False(cache.TryGet(Guid.NewGuid(), DateTime.UtcNow, out _));
    }
}
