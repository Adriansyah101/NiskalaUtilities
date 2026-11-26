using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Niskala.Guardian.Registry;

public static class RegFile
{
    public const string Header = "Windows Registry Editor Version 5.00";

    private const int StringKind = 1;
    private const int BinaryKind = 3;
    private const int DwordKind = 4;
    private const int DwordSize = 4;
    private const int WrapColumn = 76;
    private const string Continuation = "  ";

    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: true);

    public static int Write(Stream stream, RegistryBackup backup)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(backup);
        using var writer = new StreamWriter(stream, Utf16, leaveOpen: true) { NewLine = "\r\n" };
        writer.WriteLine(Header);

        int skipped = 0;
        foreach (RegistryBackupEntry entry in backup.Entries)
        {
            string key = KeyName(entry.Path);
            if (entry.Key is { } tree)
            {
                skipped += WriteKey(writer, key, tree);
            }
            else if (entry.Value is { } value)
            {
                writer.WriteLine();
                writer.WriteLine($"[{key}]");
                skipped += WriteValue(writer, value);
            }
        }

        writer.WriteLine();
        return skipped;
    }

    public static string KeyName(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text = (path.StartsWith(@"Computer\", StringComparison.OrdinalIgnoreCase) ? path[9..] : path).TrimEnd('\\');
        int split = text.IndexOf('\\');
        string root = split < 0 ? text : text[..split];
        string full = root.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKEY_LOCAL_MACHINE",
            "HKCU" or "HKEY_CURRENT_USER" => "HKEY_CURRENT_USER",
            "HKU" or "HKEY_USERS" => "HKEY_USERS",
            _ => throw new ArgumentException($"{root} is not a hive a registry file can name.", nameof(path)),
        };

        return split < 0 ? full : full + text[split..];
    }

    private static int WriteKey(StreamWriter writer, string path, RegistryKeyBackup key)
    {
        writer.WriteLine();
        writer.WriteLine($"[{path}]");
        int skipped = 0;
        foreach (RegistryValueBackup value in key.Values)
        {
            skipped += WriteValue(writer, value);
        }

        foreach (RegistryKeyBackup child in key.SubKeys)
        {
            skipped += WriteKey(writer, $@"{path}\{child.Name}", child);
        }

        return skipped;
    }

    private static int WriteValue(StreamWriter writer, RegistryValueBackup value)
    {
        if (value.Name.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return 1;
        }

        string name = value.Name.Length == 0 ? "@" : Quote(value.Name);
        string? plain = value.Kind switch
        {
            StringKind when PlainString(value.Data) is { } text => Quote(text),
            DwordKind when value.Data.Length == DwordSize =>
                "dword:" + BinaryPrimitives.ReadUInt32LittleEndian(value.Data).ToString("x8", CultureInfo.InvariantCulture),
            _ => null,
        };

        if (plain is not null)
        {
            writer.WriteLine($"{name}={plain}");
        }
        else
        {
            string type = value.Kind == BinaryKind ? "hex:" : string.Create(CultureInfo.InvariantCulture, $"hex({(uint)value.Kind:x}):");
            WriteHex(writer, $"{name}={type}", value.Data);
        }

        return 0;
    }

    private static void WriteHex(StreamWriter writer, string lead, byte[] data)
    {
        var line = new StringBuilder(lead);
        for (int i = 0; i < data.Length; i++)
        {
            line.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            if (i == data.Length - 1)
            {
                break;
            }

            line.Append(',');
            if (line.Length > WrapColumn)
            {
                writer.WriteLine(line.Append('\\').ToString());
                line.Clear().Append(Continuation);
            }
        }

        writer.WriteLine(line.ToString());
    }

    private static string? PlainString(byte[] data)
    {
        if (data.Length < 2 || data.Length % 2 != 0 || data[^1] != 0 || data[^2] != 0)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(data, 0, data.Length - 2);
        return text.AsSpan().IndexOfAny('\0', '\r', '\n') < 0 && Encoding.Unicode.GetBytes(text).AsSpan().SequenceEqual(data.AsSpan(0, data.Length - 2))
            ? text
            : null;
    }

    private static string Quote(string text) => $"\"{text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
