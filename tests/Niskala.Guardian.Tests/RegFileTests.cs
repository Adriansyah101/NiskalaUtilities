using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Registry;
using Niskala.Interop.Registry;

namespace Niskala.Guardian.Tests;

public sealed class RegFileTests
{
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(30);

    private static readonly RawRegistryValue[] Values =
    [
        new(string.Empty, 1, Utf16("default\0")),
        new("Quote \"and\" back\\slash", 1, Utf16("C:\\Program Files\\Acme \"x\"\0")),
        new("Unterminated", 1, Utf16("abc")),
        new("OddLength", 1, [0x41, 0x00, 0x42]),
        new("Expand", 2, Utf16("%TEMP%\\acme\0")),
        new("Binary", 3, [.. Enumerable.Range(0, 100).Select(i => (byte)i)]),
        new("EmptyBinary", 3, []),
        new("Dword", 4, BitConverter.GetBytes(0xDEADBEEF)),
        new("ShortDword", 4, [1, 2]),
        new("Multi", 7, Utf16("a\0b\0\0")),
        new("Qword", 11, BitConverter.GetBytes(long.MaxValue)),
        new("None", 0, []),
        new("Unusual", 0x4242, [9, 9]),
        new("Ünïcödé 名前", 1, Utf16("値\0")),
    ];

    [Fact]
    public void Registry_editor_import_puts_back_every_value_byte_for_byte()
    {
        using var scratch = new RegistryScratch();
        using var folder = new ScratchFolder();
        scratch.Create(@"App\Sub\Deep").Dispose();
        Write(scratch.Path("App"), Values);
        Write(scratch.Path(@"App\Sub\Deep"), [new("Level", 4, BitConverter.GetBytes(3))]);
        RegistryPath path = Parse(scratch.Path("App"));

        RegistryKeyBackup before;
        using (SafeRegistryHandle key = Open(path, writable: true))
        {
            before = RegistryEraser.TryCapture(key, path, out string? problem) ?? throw new InvalidOperationException(problem);
            Assert.Equal(0, RegistryEraser.TryDeleteTree(key, path, out _));
        }

        string file = Path.Combine(folder.Root, "app.reg");
        using (FileStream stream = File.Create(file))
        {
            Assert.Equal(0, RegFile.Write(stream, new RegistryBackup([new RegistryBackupEntry(scratch.Path("App"), before, null)])));
        }

        Import(file);

        using SafeRegistryHandle restored = Open(path, writable: false);
        RegistryKeyBackup after = RegistryEraser.TryCapture(restored, path, out string? again) ?? throw new InvalidOperationException(again);
        Assert.Equal(Flatten(string.Empty, before), Flatten(string.Empty, after));
    }

    [Fact]
    public void Writes_the_registry_editor_format()
    {
        var backup = new RegistryBackup(
        [
            new RegistryBackupEntry(@"HKCU\Software\Acme", new RegistryKeyBackup("Acme", [Backup(Values[0]), Backup(Values[1]), Backup(Values[2]), Backup(Values[5]), Backup(Values[7])], []), null),
            new RegistryBackupEntry(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", null, Backup(new RawRegistryValue("Acme", 1, Utf16("acme.exe\0")))),
        ]);

        using var stream = new MemoryStream();
        Assert.Equal(0, RegFile.Write(stream, backup));
        byte[] bytes = stream.ToArray();
        Assert.Equal([0xFF, 0xFE], bytes[..2]);

        string[] lines = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Split("\r\n");
        Assert.Equal(RegFile.Header, lines[0]);
        Assert.Contains(@"[HKEY_CURRENT_USER\Software\Acme]", lines);
        Assert.Contains("@=\"default\"", lines);
        Assert.Contains("\"Quote \\\"and\\\" back\\\\slash\"=\"C:\\\\Program Files\\\\Acme \\\"x\\\"\"", lines);
        Assert.Contains("\"Unterminated\"=hex(1):61,00,62,00,63,00", lines);
        Assert.Contains("\"Dword\"=dword:deadbeef", lines);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run]", lines);
        Assert.Contains("\"Acme\"=\"acme.exe\"", lines);
        Assert.All(lines, line => Assert.True(line.Length <= 80, line));
        Assert.Equal(string.Empty, lines[^1]);
    }

    [Fact]
    public void Leaves_out_value_names_a_registry_file_cannot_hold()
    {
        var key = new RegistryKeyBackup("Acme", [new RegistryValueBackup("Two\nLines", 1, Utf16("x\0")), Backup(Values[0])], []);
        using var stream = new MemoryStream();

        Assert.Equal(1, RegFile.Write(stream, new RegistryBackup([new RegistryBackupEntry(@"HKCU\Software\Acme", key, null)])));
        Assert.DoesNotContain("Two", Encoding.Unicode.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Acme", @"HKEY_LOCAL_MACHINE\SOFTWARE\Acme")]
    [InlineData(@"Computer\HKEY_CURRENT_USER\Software\Acme\", @"HKEY_CURRENT_USER\Software\Acme")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Acme", @"HKEY_USERS\S-1-5-21-1-2-3-1001\Software\Acme")]
    public void Names_keys_by_their_full_hive(string path, string expected)
    {
        Assert.Equal(expected, RegFile.KeyName(path));
    }

    [Fact]
    public void Refuses_hives_a_registry_file_cannot_name()
    {
        Assert.Throws<ArgumentException>(() => RegFile.KeyName(@"HKCR\.acme"));
    }

    [Fact]
    public void Journal_keeps_a_registry_editor_copy_beside_the_backup()
    {
        using var folder = new ScratchFolder();
        var store = new TransactionStore(folder.Root);
        string id = store.NewId();
        var key = new RegistryKeyBackup("Acme", [Backup(Values[0])], []);

        Assert.Equal(0, store.SaveRegistryBackup(id, new RegistryBackup([new RegistryBackupEntry(@"HKCU\Software\Acme", key, null)])));

        string export = store.RegistryExportPath(id);
        Assert.StartsWith(RegFile.Header, File.ReadAllText(export, Encoding.Unicode).TrimStart('\uFEFF'), StringComparison.Ordinal);
        Assert.NotNull(store.LoadRegistryBackup(id));
    }

    private static void Import(string file)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("import");
        start.ArgumentList.Add(file);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("reg.exe did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(ImportTimeout), "reg.exe import did not finish");
        Assert.True(process.ExitCode == 0, $"reg.exe import: {output.Result} {error.Result}");
    }

    private static IEnumerable<string> Flatten(string parent, RegistryKeyBackup key)
    {
        string path = parent.Length == 0 ? key.Name : $@"{parent}\{key.Name}";
        return key.Values.Select(v => $"{path}|{v.Name}|{v.Kind}|{Convert.ToHexString(v.Data)}").Order()
            .Concat(key.SubKeys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).SelectMany(k => Flatten(path, k)));
    }

    private static RegistryValueBackup Backup(RawRegistryValue value) => new(value.Name, value.Kind, value.Data);

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

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
}
