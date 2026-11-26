using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Niskala.Interop.Msi;

namespace Niskala.Scanner.Inventory.Msi;

public interface IMsiComponentPaths
{
    IReadOnlyList<string> GetComponentPaths(Guid productCode);
}

public sealed class MsiComponentIndex(IEnumerable<Guid> products, ILogger<MsiComponentIndex> logger) : IMsiComponentPaths
{
    private readonly HashSet<string> _wanted = [.. products.Select(Format)];
    private Dictionary<string, List<ComponentRef>>? _index;

    public IReadOnlyList<string> GetComponentPaths(Guid productCode)
    {
        string code = Format(productCode);
        _index ??= Build();
        if (!_index.TryGetValue(code, out List<ComponentRef>? components))
        {
            return [];
        }

        var paths = new List<string>(components.Count);
        foreach (ComponentRef component in components)
        {
            if (MsiNative.GetComponentPath(code, component.ComponentCode, component.ProductContext, out string? path) == MsiComponentState.Local
                && path is { Length: > 3 }
                && !IsRegistryKeyPath(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private Dictionary<string, List<ComponentRef>> Build()
    {
        long start = Stopwatch.GetTimestamp();
        var index = new Dictionary<string, List<ComponentRef>>(StringComparer.OrdinalIgnoreCase);

        int rc = MsiNative.TryEnumerateComponents(out List<string> components);
        if (rc != MsiNative.ErrorSuccess)
        {
            ScannerLog.MsiCallFailed(logger, "MsiEnumComponentsEx", "*", rc);
        }

        foreach (string component in components)
        {
            if (MsiNative.TryEnumerateClients(component, out List<MsiClient> clients) != MsiNative.ErrorSuccess)
            {
                continue;
            }

            foreach (MsiClient client in clients.Where(c => _wanted.Contains(c.ProductCode)))
            {
                if (!index.TryGetValue(client.ProductCode, out List<ComponentRef>? list))
                {
                    list = [];
                    index.Add(client.ProductCode, list);
                }

                list.Add(new ComponentRef(component, client.Context));
            }
        }

        ScannerLog.MsiIndexBuilt(logger, components.Count, index.Count, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return index;
    }

    private static string Format(Guid code) => code.ToString("B").ToUpperInvariant();

    private static bool IsRegistryKeyPath(string path) =>
        path.Length >= 3 && char.IsAsciiDigit(path[0]) && char.IsAsciiDigit(path[1]) && path[2] == ':';

    private readonly record struct ComponentRef(string ComponentCode, MsiContext ProductContext);
}
