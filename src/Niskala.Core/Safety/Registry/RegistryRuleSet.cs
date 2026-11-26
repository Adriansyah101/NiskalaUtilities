namespace Niskala.Core.Safety.Registry;

public sealed class RegistryRuleSet
{
    private readonly Node _machine = new();
    private readonly Node _user = new();
    private readonly ProtectedRegistryValue[] _values;

    public RegistryRuleSet(IEnumerable<RegistryRule> rules, IEnumerable<ProtectedRegistryValue> protectedValues)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(protectedValues);

        foreach (RegistryRule rule in rules)
        {
            Add(rule.Root == RegistryRoot.Machine ? _machine : _user, rule);
        }

        _machine.ComputeRulesBelow();
        _user.ComputeRulesBelow();
        _values = [.. protectedValues];
    }

    public static RegistryRuleSet Default { get; } = new(DefaultRegistryRules.BuildRules(), DefaultRegistryRules.BuildProtectedValues());

    internal readonly record struct Evaluation(RegistryRule? Deepest, bool DeepestIsSelfMatch, RegistryRule? ContainedRule);

    internal Evaluation Evaluate(RegistryRoot root, IReadOnlyList<string> segments)
    {
        Node start = root == RegistryRoot.Machine ? _machine : _user;
        RegistryRule? deepest = start.Rule;
        int deepestDepth = start.Rule is null ? -1 : 0;

        List<Node> current = [start];
        for (int depth = 0; depth < segments.Count && current.Count > 0; depth++)
        {
            var next = new List<Node>();
            foreach (Node node in current)
            {
                node.CollectNext(segments[depth], next);
            }

            foreach (Node node in next)
            {
                if (node.Rule is { } rule
                    && (depth + 1 > deepestDepth || (depth + 1 == deepestDepth && rule.Kind == ProtectionKind.Tree)))
                {
                    deepest = rule;
                    deepestDepth = depth + 1;
                }
            }

            current = next;
        }

        RegistryRule? contained = current.Select(node => node.FirstRuleBelow).FirstOrDefault(rule => rule is not null);
        return new Evaluation(deepest, deepestDepth == segments.Count, contained);
    }

    internal ProtectedRegistryValue? FindProtectedValue(RegistryRoot root, IReadOnlyList<string> segments, string valueName)
    {
        string keyPath = string.Join('\\', segments);
        return _values.FirstOrDefault(value =>
            value.Root == root
            && string.Equals(value.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
    }

    private static void Add(Node root, RegistryRule rule)
    {
        Node node = root;
        foreach (string token in rule.Pattern)
        {
            node = node.GetOrAddChild(token);
        }

        if (node.Rule is null || (node.Rule.Kind == ProtectionKind.Self && rule.Kind == ProtectionKind.Tree))
        {
            node.Rule = rule;
        }
    }

    private sealed class Node
    {
        private readonly Dictionary<string, Node> _literal = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, Node>> _prefix = [];
        private Node? _any;

        public RegistryRule? Rule { get; set; }

        public RegistryRule? FirstRuleBelow { get; private set; }

        public Node GetOrAddChild(string token)
        {
            if (token == RegistryRule.AnyKey)
            {
                return _any ??= new Node();
            }

            if (RegistryRule.TryGetPrefix(token, out string prefix))
            {
                foreach (KeyValuePair<string, Node> pair in _prefix)
                {
                    if (string.Equals(pair.Key, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }

                var created = new Node();
                _prefix.Add(new KeyValuePair<string, Node>(prefix, created));
                return created;
            }

            if (!_literal.TryGetValue(token, out Node? child))
            {
                child = new Node();
                _literal.Add(token, child);
            }

            return child;
        }

        public void CollectNext(string segment, List<Node> into)
        {
            if (_literal.TryGetValue(segment, out Node? literal))
            {
                into.Add(literal);
            }

            foreach (KeyValuePair<string, Node> pair in _prefix)
            {
                if (segment.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    into.Add(pair.Value);
                }
            }

            if (_any is not null)
            {
                into.Add(_any);
            }
        }

        public RegistryRule? ComputeRulesBelow()
        {
            RegistryRule? below = null;
            foreach (Node child in Children())
            {
                RegistryRule? fromChild = child.ComputeRulesBelow();
                below ??= fromChild;
            }

            FirstRuleBelow = below;
            return Rule ?? below;
        }

        private IEnumerable<Node> Children()
        {
            foreach (Node child in _literal.Values)
            {
                yield return child;
            }

            foreach (KeyValuePair<string, Node> pair in _prefix)
            {
                yield return pair.Value;
            }

            if (_any is not null)
            {
                yield return _any;
            }
        }
    }
}
