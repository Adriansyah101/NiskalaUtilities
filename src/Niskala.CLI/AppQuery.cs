using Niskala.Core.Models;

namespace Niskala.Cli;

internal static class AppQuery
{
    private const int ListLimit = 24;

    public static List<InstalledApp> Find(IEnumerable<InstalledApp> apps, string query)
    {
        List<InstalledApp> candidates = [.. apps];
        List<InstalledApp> byId = [.. candidates.Where(a => a.Id.Equals(query, StringComparison.OrdinalIgnoreCase))];
        if (byId.Count > 0)
        {
            return byId;
        }

        List<InstalledApp> byName = [.. candidates.Where(a => string.Equals(a.DisplayName, query, StringComparison.CurrentCultureIgnoreCase))];
        return byName.Count > 0
            ? byName
            : [.. candidates.Where(a => a.DisplayName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)];
    }

    public static async Task ReportNoSingleMatch(string query, IReadOnlyList<InstalledApp> matches, string hint)
    {
        TextWriter error = Console.Error;
        if (matches.Count == 0)
        {
            await error.WriteLineAsync($"No application matches \"{query}\". {hint}").ConfigureAwait(false);
            return;
        }

        await error.WriteLineAsync($"\"{query}\" matches {matches.Count} applications; pass one of these ids:").ConfigureAwait(false);
        foreach (InstalledApp app in matches.Take(ListLimit))
        {
            await error.WriteLineAsync($"  {app.Id}  {app.DisplayName} {app.DisplayVersion}").ConfigureAwait(false);
        }
    }
}
