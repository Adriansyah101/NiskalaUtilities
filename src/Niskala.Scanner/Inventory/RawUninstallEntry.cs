using System.Globalization;
using Niskala.Core.Models;

namespace Niskala.Scanner.Inventory;

public sealed class RawUninstallEntry
{
    public required InstallScope Scope { get; init; }

    public required RegistryViewKind View { get; init; }

    public required string KeyPath { get; init; }

    public required string KeyName { get; init; }

    public DateTime? LastWriteTimeUtc { get; init; }

    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    public bool Has(string name) => Values.ContainsKey(name);

    public string? GetString(string name)
    {
        if (!Values.TryGetValue(name, out object? value))
        {
            return null;
        }

        string? text = value switch
        {
            string s => s,
            string[] { Length: > 0 } multi => multi[0],
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        text = text?.TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public long? GetNumber(string name)
    {
        if (!Values.TryGetValue(name, out object? value))
        {
            return null;
        }

        return value switch
        {
            int i => (uint)i,
            long l => l,
            string s when long.TryParse(s.Trim().TrimEnd('\0'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            string s when s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(s.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) => hex,
            _ => null,
        };
    }

    public bool GetFlag(string name) => GetNumber(name) is { } n && n != 0;
}
