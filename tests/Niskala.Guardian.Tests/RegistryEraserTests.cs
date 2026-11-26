using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Registry;
using Niskala.Interop.Registry;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Guardian.Tests;

public sealed class RegistryEraserTests
{
    private static readonly RawRegistryValue[] Values =
    [
        new(string.Empty, 1, Encoding.Unicode.GetBytes("default\0")),
        new("Expand", 2, Encoding.Unicode.GetBytes("%TEMP%\\acme\0")),
        new("Binary", 3, [1, 2, 3]),
        new("Dword", 4, BitConverter.GetBytes(7)),
        new("Multi", 7, Encoding.Unicode.GetBytes("a\0b\0\0")),
        new("Qword", 11, BitConverter.GetBytes(long.MaxValue)),
        new("None", 0, []),
        new("Unusual", 0x4242, [9, 9]),
    ];

    [Fact]
    public void Backup_delete_and_restore_keep_every_value_type_byte_for_byte()
    {
        using var scratch = new RegistryScratch();
        scratch.Create(@"App\Settings\Deep").Dispose();
        Write(scratch.Path("App"), Values);
        Write(scratch.Path(@"App\Settings\Deep"), [new("Level", 4, BitConverter.GetBytes(3))]);
        RegistryPath path = Parse(scratch.Path("App"));

        RegistryKeyBackup backup;
        using (SafeRegistryHandle key = Open(path, writable: true))
        {
            backup = RegistryEraser.TryCapture(key, path, out string? problem) ?? throw new InvalidOperationException(problem);
            Assert.Equal(0, RegistryEraser.TryDeleteTree(key, path, out _));
        }

        Assert.Equal("Deep", Assert.Single(Assert.Single(backup.SubKeys).SubKeys).Name);
        Assert.Null(Hives.CurrentUser.OpenSubKey($@"{scratch.Key}\App"));

        Assert.Equal(0, RegistryEraser.TryRestore(path, backup, out RestoreCounts counts));
        Assert.Equal(new RestoreCounts(Values.Length + 1, 0), counts);
        using (SafeRegistryHandle key = Open(path, writable: false))
        {
            Assert.Equal(0, RegistryNative.TryReadValues(key, out List<RawRegistryValue> restored));
            Assert.Equal(Values.Select(Text).Order(), restored.Select(Text).Order());
        }

        using RegistryKey? deep = Hives.CurrentUser.OpenSubKey($@"{scratch.Key}\App\Settings\Deep");
        Assert.Equal(3, deep?.GetValue("Level"));
    }

    [Fact]
    public void Restore_writes_only_values_that_are_missing()
    {
        using var scratch = new RegistryScratch();
        using (RegistryKey key = scratch.Create("App"))
        {
            key.SetValue("Kept", "old");
            key.SetValue("Missing", "back");
        }

        RegistryPath path = Parse(scratch.Path("App"));
        RegistryKeyBackup backup;
        using (SafeRegistryHandle key = Open(path, writable: false))
        {
            backup = RegistryEraser.TryCapture(key, path, out _)!;
        }

        using (RegistryKey key = scratch.Create("App"))
        {
            key.SetValue("Kept", "new");
            key.DeleteValue("Missing");
        }

        Assert.Equal(0, RegistryEraser.TryRestore(path, backup, out RestoreCounts counts));
        Assert.Equal(new RestoreCounts(1, 1), counts);
        using RegistryKey app = scratch.Create("App");
        Assert.Equal("new", app.GetValue("Kept"));
        Assert.Equal("back", app.GetValue("Missing"));
    }

    [Fact]
    public void A_registry_link_in_the_tree_is_refused_and_its_target_survives()
    {
        using var scratch = new RegistryScratch();
        using (RegistryKey target = scratch.Create("Target"))
        {
            target.SetValue("Keep", "yes");
        }

        using (RegistryKey app = scratch.Create("App"))
        {
            app.SetValue("Own", 1);
        }

        scratch.Link(@"App\Link", "Target");
        RegistryPath path = Parse(scratch.Path("App"));
        using (SafeRegistryHandle key = Open(path, writable: true))
        {
            Assert.Null(RegistryEraser.TryCapture(key, path, out string? captureProblem));
            Assert.Contains("registry link", captureProblem, StringComparison.Ordinal);
            Assert.NotEqual(0, RegistryEraser.TryDeleteTree(key, path, out string? deleteProblem));
            Assert.Contains("registry link", deleteProblem, StringComparison.Ordinal);
        }

        RegistryPath link = Parse(scratch.Path(@"App\Link"));
        using (SafeRegistryHandle key = Open(link, writable: true))
        {
            Assert.True(RegistryNative.IsLink(key));
            Assert.Null(RegistryEraser.TryCapture(key, link, out _));
        }

        using RegistryKey? survivor = Hives.CurrentUser.OpenSubKey($@"{scratch.Key}\Target");
        Assert.Equal("yes", survivor?.GetValue("Keep"));
    }

    private static RegistryPath Parse(string path)
    {
        Assert.Equal(RegistryVerdict.Allowed, RegistryPath.TryParse(path, RegistryView.Default, out RegistryPath? parsed));
        return parsed!;
    }

    private static SafeRegistryHandle Open(RegistryPath path, bool writable)
    {
        Assert.Equal(0, RegistryEraser.TryOpen(path, writable, out SafeRegistryHandle key));
        return key;
    }

    private static void Write(string path, IEnumerable<RawRegistryValue> values)
    {
        using SafeRegistryHandle key = Open(Parse(path), writable: true);
        foreach (RawRegistryValue value in values)
        {
            Assert.Equal(0, RegistryNative.TrySetValue(key, value));
        }
    }

    private static string Text(RawRegistryValue value) => $"{value.Name}|{value.Kind}|{Convert.ToHexString(value.Data)}";
}
