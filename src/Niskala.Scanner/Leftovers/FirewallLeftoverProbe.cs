using System.Globalization;
using Niskala.Interop.Firewall;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Scanner.Leftovers;

public interface IFirewallRuleSource
{
    int TryList(out List<FirewallRule> rules);
}

public sealed class SystemFirewallRuleSource : IFirewallRuleSource
{
    public static SystemFirewallRuleSource Instance { get; } = new();

    public int TryList(out List<FirewallRule> rules) => FirewallNative.TryList(out rules);
}

public sealed class FirewallLeftoverProbe(IFirewallRuleSource source)
{
    internal void Find(ScanContext context, CandidateSet candidates, List<string> notes)
    {
        Fingerprint fingerprint = context.Fingerprint;
        if (fingerprint.Roots.Count == 0 && fingerprint.Services.Count == 0)
        {
            return;
        }

        int error = source.TryList(out List<FirewallRule> rules);
        if (error != 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Firewall rules could not be read (0x{error:X8})."));
            return;
        }

        foreach (IGrouping<string, FirewallRule> group in rules.Where(r => r.Name.Length > 0).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            List<(FirewallRule Rule, LeftoverSignal Signal, string Detail)> owned = [.. group.Select(r => Evidence(context, r)).OfType<(FirewallRule, LeftoverSignal, string)>()];
            if (owned.Count == 0)
            {
                continue;
            }

            if (owned.Count < group.Count())
            {
                notes.Add($"Firewall rule name \"{group.Key}\" is shared with rules for other programs; left alone.");
                continue;
            }

            foreach ((FirewallRule rule, LeftoverSignal signal, string detail) in owned)
            {
                candidates.Add(LeftoverKind.FirewallRule, group.Key, null, group.Key, null, signal, detail, rule.ApplicationName);
            }

            if (TokenNormalizer.MatchProduct(group.Key, context.Tokens) is { Kind: NameMatchKind.Exact, Token: { } token })
            {
                candidates.Add(LeftoverKind.FirewallRule, group.Key, null, group.Key, null, LeftoverSignal.ExactName, $"rule name is \"{token.Text}\"");
            }
        }
    }

    private static (FirewallRule, LeftoverSignal, string)? Evidence(ScanContext context, FirewallRule rule)
    {
        if (context.Owner.OwnsPath(rule.ApplicationName))
        {
            return (rule, LeftoverSignal.PointsToFingerprint, $"{Direction(rule)} rule for {rule.ApplicationName}");
        }

        return rule.ServiceName is { Length: > 0 } service
            && context.Fingerprint.Services.Any(s => s.Name.Equals(service, StringComparison.OrdinalIgnoreCase))
            ? (rule, LeftoverSignal.InFingerprint, $"{Direction(rule)} rule for service {service}, recorded before the uninstall")
            : null;
    }

    private static string Direction(FirewallRule rule) => rule.Direction == 2 ? "outbound" : "inbound";
}
