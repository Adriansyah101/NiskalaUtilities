namespace Niskala.Core.Safety.Registry;

public sealed record RegistryRule(RegistryRoot Root, IReadOnlyList<string> Pattern, ProtectionKind Kind, string Reason)
{
    public const string AnyKey = "{any}";

    private const string PrefixStart = "{prefix:";

    public override string ToString() =>
        $"{Kind} {(Root == RegistryRoot.Machine ? "HKLM" : "USER")}\\{string.Join('\\', Pattern)} ({Reason})";

    internal static bool TryGetPrefix(string token, out string prefix)
    {
        if (token.StartsWith(PrefixStart, StringComparison.Ordinal) && token.EndsWith('}'))
        {
            prefix = token[PrefixStart.Length..^1];
            return prefix.Length > 0;
        }

        prefix = string.Empty;
        return false;
    }
}

public sealed record ProtectedRegistryValue(RegistryRoot Root, string KeyPath, string ValueName, string Reason, bool ContentRevertible = false);
