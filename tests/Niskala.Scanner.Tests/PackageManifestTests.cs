using System.Text;
using Niskala.Scanner.Inventory;

namespace Niskala.Scanner.Tests;

public sealed class PackageManifestTests
{
    private const string Sparse = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
          <Properties>
            <DisplayName>Acme Shell Extension</DisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent> true </uap10:AllowExternalContent>
          </Properties>
          <Applications>
            <Application Id="App">
              <uap:VisualElements DisplayName="Acme" Square44x44Logo="Assets\AppList.png" Square150x150Logo="Assets\Tile.png" BackgroundColor="#0078d7" />
            </Application>
            <Application Id="Second">
              <uap:VisualElements DisplayName="Other" Square44x44Logo="Assets\Other.png" BackgroundColor="transparent" />
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void Reads_the_sparse_flag_and_the_first_app_list_logo_with_its_plate()
    {
        using var scratch = new ScratchFolder();
        scratch.File("AppxManifest.xml", Encoding.UTF8.GetBytes(Sparse));

        PackageManifest manifest = PackageManifest.Read(scratch.Root);

        Assert.True(manifest.AllowsExternalContent);
        Assert.Equal(Path.Combine(scratch.Root, @"Assets\AppList.png"), manifest.AppListLogo);
        Assert.Equal("#0078D7", manifest.LogoBackground);
    }

    [Theory]
    [InlineData("#1e1e1e", "#1E1E1E")]
    [InlineData(" Blue ", "blue")]
    [InlineData("transparent", null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalizes_background_colors(string? value, string? expected)
    {
        Assert.Equal(expected, PackageManifest.NormalizeColor(value));
    }

    [Fact]
    public void Regular_package_is_not_sparse()
    {
        using var scratch = new ScratchFolder();
        scratch.File("AppxManifest.xml", Encoding.UTF8.GetBytes(Sparse.Replace("<uap10:AllowExternalContent> true </uap10:AllowExternalContent>", string.Empty)));

        Assert.False(PackageManifest.Read(scratch.Root).AllowsExternalContent);
    }

    [Theory]
    [InlineData("<Package><Unclosed>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><Package>&e;</Package>")]
    public void Broken_or_hostile_manifest_reads_as_empty(string content)
    {
        using var scratch = new ScratchFolder();
        scratch.File("AppxManifest.xml", Encoding.UTF8.GetBytes(content));

        Assert.Equal(PackageManifest.Empty, PackageManifest.Read(scratch.Root));
    }

    [Fact]
    public void Missing_manifest_reads_as_empty()
    {
        using var scratch = new ScratchFolder();
        Assert.Equal(PackageManifest.Empty, PackageManifest.Read(scratch.Root));
    }
}
