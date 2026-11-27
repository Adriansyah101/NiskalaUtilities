using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Interop.Icons;
using Niskala.Scanner.Icons;
using static Niskala.Scanner.Tests.TestEntries;

namespace Niskala.Scanner.Tests;

public sealed class IconTests
{
    private static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    private static readonly string Shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");

    private static IconSourceResolver Resolver(ICommandEnvironment environment, string? productIcon = null) =>
        new(environment, (_, _) => productIcon);

    [Theory]
    [InlineData(@"C:\Acme\acme.exe", @"C:\Acme\acme.exe", 0)]
    [InlineData(@"C:\Acme\acme.exe,3", @"C:\Acme\acme.exe", 3)]
    [InlineData(@"""C:\Acme Suite\acme.exe"",-101", @"C:\Acme Suite\acme.exe", -101)]
    [InlineData(@"""C:\Acme Suite\acme.exe""", @"C:\Acme Suite\acme.exe", 0)]
    [InlineData(@"C:\Acme, Inc\acme.ico", @"C:\Acme, Inc\acme.ico", 0)]
    [InlineData(@"%SystemRoot%\System32\shell32.dll,-154", @"C:\Windows\System32\shell32.dll", -154)]
    public void Parses_icon_locations(string value, string path, int index)
    {
        IconSource? source = Resolver(new FakeEnvironment()).ParseIconLocation(value);

        Assert.Equal(new IconSource(IconSourceKind.Resource, path, index), source);
    }

    [Fact]
    public void Png_display_icon_is_an_image()
    {
        Assert.Equal(IconSourceKind.Image, Resolver(new FakeEnvironment()).ParseIconLocation(@"C:\Acme\logo.png")?.Kind);
    }

    [Fact]
    public void Resolution_order_is_display_icon_then_product_icon_then_uninstaller()
    {
        var env = new FakeEnvironment(@"C:\Acme\acme.exe", @"C:\Windows\Installer\{X}\product.ico", @"C:\Acme\uninst.exe");
        var uninstall = new ParsedCommand(@"C:\Acme\uninst.exe", @"C:\Acme\uninst.exe", string.Empty, true, CommandParseIssue.None);
        InstalledApp app = App("a") with { DisplayIcon = @"C:\Acme\acme.exe,2", InstallerKind = InstallerKind.Nsis, UninstallCommand = uninstall };

        Assert.Equal(@"C:\Acme\acme.exe", Resolver(env).Resolve(app)?.Path);

        InstalledApp msi = App("m") with { DisplayIcon = @"C:\Gone\gone.exe", InstallerKind = InstallerKind.Msi, MsiProductCode = Guid.NewGuid() };
        Assert.Equal(@"C:\Windows\Installer\{X}\product.ico", Resolver(env, @"C:\Windows\Installer\{X}\product.ico").Resolve(msi)?.Path);
        Assert.Null(Resolver(env).Resolve(msi));

        Assert.Equal(@"C:\Acme\uninst.exe", Resolver(env).Resolve(app with { DisplayIcon = null })?.Path);
        Assert.Null(Resolver(env).Resolve(app with { DisplayIcon = null, InstallerKind = InstallerKind.WingetPortable }));
    }

    [Fact]
    public void Logo_locator_picks_the_widest_variant_and_skips_high_contrast()
    {
        using var scratch = new ScratchFolder();
        scratch.File(@"Assets\AppList.scale-100.png", Png(44));
        scratch.File(@"Assets\AppList.scale-200.png", Png(88));
        string best = scratch.File(@"Assets\AppList.targetsize-256_altform-unplated.png", Png(256));
        scratch.File(@"Assets\AppList.targetsize-256_contrast-black.png", Png(512));
        scratch.File(@"Assets\AppListWide.scale-400.png", Png(1024));

        Assert.Equal(best, AppxLogoLocator.FindBest(Path.Combine(scratch.Root, @"Assets\AppList.png")));
        Assert.Equal(best, AppxLogoLocator.FindBest(Path.Combine(scratch.Root, @"Assets\AppList.scale-100.png")));
        Assert.Null(AppxLogoLocator.FindBest(Path.Combine(scratch.Root, @"Missing\AppList.png")));
    }

    [Fact]
    public void Png_encoder_writes_a_valid_rgba_image()
    {
        byte[] bgra = [10, 20, 30, 255, 1, 2, 3, 128, 0, 0, 0, 0, 200, 100, 50, 64];
        using var stream = new MemoryStream();
        PngEncoder.Write(stream, new IconPixels(2, 2, bgra));
        byte[] png = stream.ToArray();

        Assert.True(png.AsSpan(0, 8).SequenceEqual(PngEncoder.Signature));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal([8, 6], png[24..26]);
        Assert.Equal([0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82], png[^12..]);

        int idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var zlib = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        Assert.Equal([0, 30, 20, 10, 255, 3, 2, 1, 128, 0, 0, 0, 0, 0, 50, 100, 200, 64], raw.ToArray());
    }

    [Fact]
    public async Task Extracts_a_256_px_icon_from_a_system_file()
    {
        using var extractor = new IconExtractor();

        IconPixels? notepad = await extractor.ExtractAsync(new IconSource(IconSourceKind.Resource, Notepad, 0), 256, TestContext.Current.CancellationToken);
        IconPixels? folder = await extractor.ExtractAsync(new IconSource(IconSourceKind.Resource, Shell32, 3), 256, TestContext.Current.CancellationToken);

        Assert.NotNull(notepad);
        Assert.Equal((256, 256), (notepad.Width, notepad.Height));
        Assert.Contains(notepad.Bgra.Where((_, i) => i % 4 == 3), alpha => alpha is > 0 and < 255);
        Assert.NotNull(folder);
        Assert.Equal(256, folder.Width);
    }

    [Fact]
    public async Task Missing_icon_resource_returns_null()
    {
        using var extractor = new IconExtractor();
        Assert.Null(await extractor.ExtractAsync(new IconSource(IconSourceKind.Resource, Notepad, 9999), 256, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_writes_once_and_reuses_the_file()
    {
        using var scratch = new ScratchFolder();
        using var extractor = new IconExtractor();
        var cache = new IconCache(
            extractor,
            new IconSourceResolver(SystemCommandEnvironment.Instance, (_, _) => null),
            Path.Combine(scratch.Root, "icons"),
            NullLogger<IconCache>.Instance);
        InstalledApp app = App("a") with { DisplayIcon = Notepad };

        string? first = await cache.GetAsync(app, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        DateTime written = File.GetLastWriteTimeUtc(first);

        string? second = await cache.GetAsync(app, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second!));
        Assert.Equal(256, AppxLogoLocator.PngWidth(first));
        Assert.Single(Directory.GetFiles(Path.Combine(scratch.Root, "icons")));
        Assert.Null(await cache.GetAsync(App("none"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_requests_for_one_icon_share_the_file()
    {
        using var scratch = new ScratchFolder();
        using var extractor = new IconExtractor();
        var cache = new IconCache(
            extractor,
            new IconSourceResolver(SystemCommandEnvironment.Instance, (_, _) => null),
            Path.Combine(scratch.Root, "icons"),
            NullLogger<IconCache>.Instance);

        string?[] files = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => cache.GetAsync(App($"a{i}") with { DisplayIcon = Notepad }, TestContext.Current.CancellationToken)));

        Assert.Single(files.Distinct());
        Assert.NotNull(files[0]);
        Assert.Single(Directory.GetFiles(Path.Combine(scratch.Root, "icons")));
    }

    [Fact]
    public async Task Prune_deletes_only_cache_files_no_current_app_maps_to()
    {
        using var scratch = new ScratchFolder();
        using var extractor = new IconExtractor();
        string directory = Path.Combine(scratch.Root, "icons");
        var cache = new IconCache(
            extractor,
            new IconSourceResolver(SystemCommandEnvironment.Instance, (_, _) => null),
            directory,
            NullLogger<IconCache>.Instance);
        InstalledApp kept = App("kept") with { DisplayIcon = Notepad };
        InstalledApp removed = App("removed") with { DisplayIcon = $"{Shell32},3" };

        string? keptFile = await cache.GetAsync(kept, TestContext.Current.CancellationToken);
        string? removedFile = await cache.GetAsync(removed, TestContext.Current.CancellationToken);
        string foreign = scratch.File(@"icons\readme.png", Png(16));
        string pending = scratch.File(@"icons\0123456789abcdef0123456789abcdef.png.5d41402abc4b2a76b9719d911017c592.tmp", Png(16));

        int deleted = cache.Prune([kept]);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(keptFile));
        Assert.False(File.Exists(removedFile));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(pending));
    }

    [Fact]
    public async Task File_icons_are_cached_apart_from_app_icons()
    {
        using var scratch = new ScratchFolder();
        using var extractor = new IconExtractor();
        string directory = Path.Combine(scratch.Root, "icons");
        var cache = new IconCache(
            extractor,
            new IconSourceResolver(SystemCommandEnvironment.Instance, (_, _) => null),
            directory,
            NullLogger<IconCache>.Instance);

        string? file = await cache.GetFileAsync(Notepad, TestContext.Current.CancellationToken);

        Assert.NotNull(file);
        Assert.Equal(Path.Combine(directory, IconCache.FilesFolder), Path.GetDirectoryName(file));
        Assert.Equal(256, AppxLogoLocator.PngWidth(file));
        Assert.Equal(0, cache.Prune([]));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task File_without_an_embedded_icon_gets_no_generic_shell_icon()
    {
        using var scratch = new ScratchFolder();
        using var extractor = new IconExtractor();
        var cache = new IconCache(
            extractor,
            new IconSourceResolver(SystemCommandEnvironment.Instance, (_, _) => null),
            Path.Combine(scratch.Root, "icons"),
            NullLogger<IconCache>.Instance);
        string plain = scratch.File(@"bin\plain.exe", "MZ"u8.ToArray());

        Assert.Null(await cache.GetFileAsync(plain, TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetFileAsync(Path.Combine(scratch.Root, "missing.exe"), TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetFileAsync("notepad.exe", TestContext.Current.CancellationToken));
        string files = Path.Combine(scratch.Root, "icons", IconCache.FilesFolder);
        Assert.Empty(Directory.Exists(files) ? Directory.GetFiles(files) : []);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef.png", false)]
    [InlineData("0123456789abcdef0123456789abcdef.pngx", false)]
    [InlineData("0123456789abcdef.png", false)]
    public void Recognizes_cache_file_names(string name, bool expected)
    {
        Assert.Equal(expected, IconCache.IsCacheFileName(name));
    }

    private static byte[] Png(int width)
    {
        byte[] png = new byte[33];
        PngEncoder.Signature.CopyTo(png);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), width);
        return png;
    }
}
