using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Niskala.Core.Safety.Registry;

public enum RegistryRoot
{
    Machine,
    User,
}

public sealed partial record RegistryPath
{
    private const int MaxKeyNameLength = 255;

    public required RegistryRoot Root { get; init; }

    public string? UserSid { get; init; }

    public required IReadOnlyList<string> Segments { get; init; }

    public required RegistryHive Hive { get; init; }

    public required string PhysicalSubKey { get; init; }

    public required RegistryView View { get; init; }

    public string LogicalPath =>
        (Root == RegistryRoot.Machine ? "HKLM" : UserSid is null ? "HKCU" : $"HKU\\{UserSid}")
        + (Segments.Count == 0 ? string.Empty : "\\" + string.Join('\\', Segments));

    public override string ToString() => View == RegistryView.Registry32 ? LogicalPath + " [32-bit]" : LogicalPath;

    public static RegistryVerdict TryParse(string? input, RegistryView view, out RegistryPath? path)
    {
        path = null;
        if (string.IsNullOrEmpty(input))
        {
            return RegistryVerdict.InvalidSyntax;
        }

        foreach (char c in input)
        {
            if (c < 0x20)
            {
                return RegistryVerdict.InvalidSyntax;
            }
        }

        string text = input.StartsWith(@"Computer\", StringComparison.OrdinalIgnoreCase) ? input[9..] : input;
        string[] parts = text.TrimEnd('\\').Split('\\');
        if (parts.Any(p => p.Length == 0 || p.Length > MaxKeyNameLength))
        {
            return RegistryVerdict.InvalidSyntax;
        }

        RegistryView effectiveView = view == RegistryView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;
        List<string> rest = [.. parts.Skip(1)];

        switch (parts[0].ToUpperInvariant())
        {
            case "HKLM" or "HKEY_LOCAL_MACHINE":
            {
                List<string> physical = [.. rest];
                if (FoldWow64(rest, physical))
                {
                    effectiveView = RegistryView.Registry32;
                }

                path = new RegistryPath
                {
                    Root = RegistryRoot.Machine,
                    Segments = rest,
                    Hive = RegistryHive.LocalMachine,
                    PhysicalSubKey = string.Join('\\', physical),
                    View = effectiveView,
                };
                break;
            }

            case "HKCU" or "HKEY_CURRENT_USER":
                path = new RegistryPath
                {
                    Root = RegistryRoot.User,
                    Segments = rest,
                    Hive = RegistryHive.CurrentUser,
                    PhysicalSubKey = string.Join('\\', rest),
                    View = effectiveView,
                };
                break;

            case "HKU" or "HKEY_USERS":
            {
                if (rest.Count == 0)
                {
                    return RegistryVerdict.HiveRoot;
                }

                string hiveName = rest[0];
                bool isClasses = hiveName.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);
                string sid = isClasses ? hiveName[..^"_Classes".Length] : hiveName;

                if (sid.Equals(".DEFAULT", StringComparison.OrdinalIgnoreCase) || SystemAccountSid().IsMatch(sid))
                {
                    return RegistryVerdict.SystemAccountHive;
                }

                if (!UserSidPattern().IsMatch(sid))
                {
                    return RegistryVerdict.UnknownHive;
                }

                List<string> logical = isClasses ? ["Software", "Classes", .. rest.Skip(1)] : [.. rest.Skip(1)];
                if (logical.Count == 0)
                {
                    return RegistryVerdict.HiveRoot;
                }

                path = new RegistryPath
                {
                    Root = RegistryRoot.User,
                    UserSid = sid.ToUpperInvariant(),
                    Segments = logical,
                    Hive = RegistryHive.Users,
                    PhysicalSubKey = string.Join('\\', rest),
                    View = effectiveView,
                };
                break;
            }

            case "HKCR" or "HKEY_CLASSES_ROOT":
                return RegistryVerdict.MergedClassesView;

            case "HKCC" or "HKEY_CURRENT_CONFIG" or "HKPD" or "HKEY_PERFORMANCE_DATA" or "HKEY_DYN_DATA":
                return RegistryVerdict.VirtualHive;

            default:
                return RegistryVerdict.UnknownHive;
        }

        return path.Segments.Count == 0 ? RegistryVerdict.HiveRoot : RegistryVerdict.Allowed;
    }

    public RegistryKey? OpenReadOnly()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(Hive, View);
        return baseKey.OpenSubKey(PhysicalSubKey);
    }

    public static RegistryKey? OpenReadOnly(string keyPath) =>
        TryParse(keyPath, RegistryView.Default, out RegistryPath? path) == RegistryVerdict.Allowed ? path!.OpenReadOnly() : null;

    private static bool FoldWow64(List<string> logical, List<string> physical)
    {
        int index = -1;
        if (logical.Count >= 2 && Is(logical[0], "SOFTWARE") && Is(logical[1], "WOW6432Node"))
        {
            index = 1;
        }
        else if (logical.Count >= 3 && Is(logical[0], "SOFTWARE") && Is(logical[1], "Classes") && Is(logical[2], "WOW6432Node"))
        {
            index = 2;
        }

        if (index < 0)
        {
            return false;
        }

        logical.RemoveAt(index);
        physical.RemoveAt(index);
        return true;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^S-1-(5-21|12-1)(-\d+){1,14}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserSidPattern();

    [GeneratedRegex(@"^S-1-5-(18|19|20)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SystemAccountSid();
}
