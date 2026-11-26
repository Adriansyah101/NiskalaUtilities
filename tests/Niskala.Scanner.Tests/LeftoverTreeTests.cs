using Microsoft.Win32;
using Niskala.Scanner.Leftovers;

namespace Niskala.Scanner.Tests;

public sealed class LeftoverTreeTests
{
    private static readonly LeftoverReason[] Reasons = [new(LeftoverSignal.ExactName, 40, "named after the product")];

    [Fact]
    public void A_folder_expands_into_subfolders_then_files_with_size_and_date()
    {
        using var scratch = new ScratchFolder();
        scratch.File(@"Acme\b.log", 7);
        scratch.File(@"Acme\a.dat", 3);
        scratch.File(@"Acme\Cache\x.bin", 11);
        var folder = new LeftoverItem(LeftoverKind.Folder, Path.Combine(scratch.Root, "Acme"), null, 90, ConfidenceTier.Safe, Reasons);

        LeftoverNode root = LeftoverTree.Root(folder);
        LeftoverChildren children = LeftoverTree.Children(root);

        Assert.True(root.HasChildren);
        Assert.Equal(["Cache", "a.dat", "b.log"], children.Nodes.Select(n => n.Name));
        Assert.Equal([LeftoverKind.Folder, LeftoverKind.File, LeftoverKind.File], children.Nodes.Select(n => n.Kind));
        Assert.Equal(7, children.Nodes[2].Bytes);
        Assert.NotNull(children.Nodes[1].Modified);
        Assert.True(children.Nodes[0].HasChildren);
        Assert.Equal((1, 11L), (children.Nodes[0].Files, children.Nodes[0].Bytes));
        Assert.Equal(["x.bin"], LeftoverTree.Children(children.Nodes[0]).Nodes.Select(n => n.Name));
    }

    [Fact]
    public void A_junction_is_a_leaf()
    {
        using var scratch = new ScratchFolder();
        string target = Path.GetDirectoryName(scratch.File(@"Target\keep.txt", 1))!;
        scratch.Junction(@"Acme", target);
        var folder = new LeftoverItem(LeftoverKind.Folder, Path.Combine(scratch.Root, "Acme"), null, 90, ConfidenceTier.Safe, Reasons);

        LeftoverNode root = LeftoverTree.Root(folder);

        Assert.True(root.IsLink);
        Assert.Empty(LeftoverTree.Children(root).Nodes);
    }

    [Fact]
    public void A_key_expands_into_subkeys_then_values_with_type_and_data()
    {
        string key = $@"{TestRoots.Key}\Tree{Guid.NewGuid():N}";
        try
        {
            using (RegistryKey created = Registry.CurrentUser.CreateSubKey(key))
            {
                created.CreateSubKey("Settings").Dispose();
                created.SetValue(null, "default");
                created.SetValue("Path", @"%ProgramFiles%\Acme", RegistryValueKind.ExpandString);
                created.SetValue("Count", 42, RegistryValueKind.DWord);
                created.SetValue("Big", 1L << 40, RegistryValueKind.QWord);
                created.SetValue("Names", new[] { "one", "two" }, RegistryValueKind.MultiString);
                created.SetValue("Blob", new byte[] { 0xDE, 0xAD, 0x01 }, RegistryValueKind.Binary);
            }

            var item = new LeftoverItem(LeftoverKind.RegistryKey, $@"HKCU\{key}", null, 65, ConfidenceTier.Review, Reasons);

            LeftoverNode root = LeftoverTree.Root(item);
            List<LeftoverNode> nodes = [.. LeftoverTree.Children(root).Nodes];

            Assert.True(root.HasChildren);
            Assert.Equal(["Settings", string.Empty, "Big", "Blob", "Count", "Names", "Path"], nodes.Select(n => n.Name));
            Assert.Equal(LeftoverKind.RegistryKey, nodes[0].Kind);
            Assert.Equal($@"HKCU\{key}\Settings", nodes[0].Path);
            Assert.Equal(
                [
                    ("REG_SZ", "default"),
                    ("REG_QWORD", "0x0000010000000000 (1099511627776)"),
                    ("REG_BINARY", "DE AD 01"),
                    ("REG_DWORD", "0x0000002A (42)"),
                    ("REG_MULTI_SZ", "one | two"),
                    ("REG_EXPAND_SZ", @"%ProgramFiles%\Acme"),
                ],
                nodes.Skip(1).Select(n => (n.ValueType!, n.Data!)));
            Assert.All(nodes.Skip(1), n => Assert.Equal($@"HKCU\{key}", n.Path));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_chosen_part_keeps_the_leftovers_score()
    {
        var key = new LeftoverItem(LeftoverKind.RegistryKey, @"HKCU\Software\Acme", null, 65, ConfidenceTier.Review, Reasons);
        var value = new LeftoverNode(LeftoverKind.RegistryValue, @"HKCU\Software\Acme", "Path", "Path");

        LeftoverItem part = LeftoverTree.ToItem(key, value);

        Assert.Equal((LeftoverKind.RegistryValue, @"HKCU\Software\Acme", "Path", 65, ConfidenceTier.Review), (part.Kind, part.Path, part.ValueName, part.Score, part.Tier));
        Assert.Same(Reasons, part.Reasons);
    }

    [Theory]
    [InlineData(LeftoverKind.Folder, @"C:\Data\Acme", null, LeftoverKind.File, @"C:\Data\Acme\Logs\a.log", null, true)]
    [InlineData(LeftoverKind.Folder, @"C:\Data\Acme", null, LeftoverKind.Folder, @"C:\Data\AcmeOther", null, false)]
    [InlineData(LeftoverKind.RegistryKey, @"HKCU\Software\Acme", null, LeftoverKind.RegistryValue, @"HKCU\Software\Acme", "Path", true)]
    [InlineData(LeftoverKind.RegistryKey, @"HKCU\Software\Acme", null, LeftoverKind.RegistryKey, @"HKCU\Software\Acme\Sub", null, true)]
    [InlineData(LeftoverKind.RegistryKey, @"HKCU\Software\Acme", null, LeftoverKind.RegistryKey, @"HKCU\Software\AcmeOther", null, false)]
    [InlineData(LeftoverKind.RegistryValue, @"HKCU\Software\Run", "Acme", LeftoverKind.RegistryValue, @"HKCU\Software\Run", "Other", false)]
    [InlineData(LeftoverKind.RegistryValue, @"HKCU\Software\Run", "Acme", LeftoverKind.RegistryValue, @"HKCU\Software\Run", "acme", true)]
    public void A_part_lies_inside_its_leftover(LeftoverKind kind, string path, string? value, LeftoverKind partKind, string partPath, string? partValue, bool expected)
    {
        var leftover = new LeftoverItem(kind, path, value, 90, ConfidenceTier.Safe, Reasons);
        var part = new LeftoverItem(partKind, partPath, partValue, 90, ConfidenceTier.Safe, Reasons);

        Assert.Equal(expected, LeftoverTree.Contains(leftover, part));
    }

    [Theory]
    [InlineData(new[] { true, true }, true)]
    [InlineData(new[] { false, false }, false)]
    [InlineData(new[] { true, false }, null)]
    public void A_parent_is_checked_partly_when_some_children_are(bool[] children, bool? expected) =>
        Assert.Equal(expected, LeftoverTree.Combine(children.Select(c => (bool?)c)));

    [Fact]
    public void A_partly_checked_child_makes_the_parent_partly_checked() =>
        Assert.Null(LeftoverTree.Combine([true, null]));

    [Fact]
    public void Large_and_binary_data_is_shortened()
    {
        Assert.EndsWith(" …", LeftoverTree.Format(3, new byte[100]), StringComparison.Ordinal);
        Assert.Equal(513, LeftoverTree.Format(1, System.Text.Encoding.Unicode.GetBytes(new string('a', 600))).Length);
        Assert.Equal("0x01020304 (16909060)", LeftoverTree.Format(5, [1, 2, 3, 4]));
    }
}
