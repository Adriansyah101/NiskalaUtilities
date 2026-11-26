using Niskala.Core.Models;
using Niskala.Scanner.Steam;

namespace Niskala.Scanner.Tests;

public sealed class SteamLibraryTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"      "C:\\Program Files (x86)\\Steam"
                "label"     ""
                "apps"
                {
                    "228980"    "184245090"
                }
            }
            "1"
            {
                "path"      "D:\\SteamLibrary"
            }
            "contentstatsid"    "-4535"
        }
        """;

    private const string Manifest = """
        "AppState"
        {
            "appid"         "570"
            "Universe"      "1"
            "name"          "Dota \"2\""
            "installdir"    "dota 2 beta"
            "SizeOnDisk"    "41234567890"
            "InstalledDepots"
            {
                "373301" { "manifest" "123" "size" "77" }
            }
        }
        """;

    [Fact]
    public void Library_folders_come_from_both_vdf_formats()
    {
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibrary.ParseLibraryFolders(LibraryFolders));
        Assert.Equal([@"E:\Games"], SteamLibrary.ParseLibraryFolders("\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"1\" \"E:\\\\Games\" }"));
    }

    [Fact]
    public void A_manifest_gives_the_folder_and_size_with_escapes_and_nested_blocks()
    {
        SteamManifest? manifest = SteamLibrary.ParseManifest(Manifest);

        Assert.Equal(new SteamManifest(570, "Dota \"2\"", "dota 2 beta", 41_234_567_890), manifest);
    }

    [Theory]
    [InlineData(@"..")]
    [InlineData(@"..\\..\\Windows")]
    [InlineData(@"C:\\Windows")]
    [InlineData(@"common/other")]
    [InlineData(@"")]
    public void A_manifest_naming_a_folder_outside_common_is_refused(string installDir)
    {
        string text = $"\"AppState\" {{ \"appid\" \"10\" \"installdir\" \"{installDir}\" }}";

        Assert.Null(SteamLibrary.ParseManifest(text));
    }

    [Theory]
    [InlineData("\"AppState\" { \"appid\" \"10\"")]
    [InlineData("\"AppState\" { \"appid\" \"10\" } }")]
    [InlineData("\"AppState\" { \"appid\" \"10 }")]
    public void Broken_key_values_are_a_format_error(string text) =>
        Assert.Throws<FormatException>(() => VdfNode.Parse(text));

    [Fact]
    public void The_app_id_comes_from_the_uninstall_key_name()
    {
        InstalledApp app = new()
        {
            Id = "reg:hklm64:Steam App 570",
            Sources = AppSources.Registry,
            Scope = InstallScope.Machine,
            KeyName = "Steam App 570",
            InstallerKind = InstallerKind.Steam,
        };

        Assert.Equal(570, SteamLibrary.AppId(app));
        Assert.Null(SteamLibrary.AppId(app with { InstallerKind = InstallerKind.CustomExe }));
        Assert.Null(SteamLibrary.AppId("Steam App x"));
        Assert.Null(SteamLibrary.AppId("Steam App -1"));
    }

    [Fact]
    public void A_game_is_found_in_a_second_library()
    {
        string scratch = Directory.CreateDirectory(Path.Combine(TestRoots.Folder, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string client = Directory.CreateDirectory(Path.Combine(scratch, "Steam", "steamapps")).Parent!.FullName;
            string library = Directory.CreateDirectory(Path.Combine(scratch, "Library", "steamapps")).Parent!.FullName;
            File.WriteAllText(Path.Combine(client, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{client.Replace(@"\", @"\\")}\" }} \"1\" {{ \"path\" \"{library.Replace(@"\", @"\\")}\" }} }}");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_570.acf"), Manifest);

            SteamGame? game = SteamLibrary.Find(570, client);

            Assert.NotNull(game);
            Assert.Equal(library, game.Library);
            Assert.Equal(Path.Combine(library, "steamapps", "common", "dota 2 beta"), game.InstallFolder);
            Assert.Equal(Path.Combine(library, "steamapps", "appmanifest_570.acf"), game.ManifestFile);
            Assert.Null(SteamLibrary.Find(440, client));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
