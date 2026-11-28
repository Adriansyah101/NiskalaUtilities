using System.Text.RegularExpressions;

namespace Niskala.UI.Tests;

public sealed partial class DiagnosticIdTests
{
    [Fact]
    public void Every_log_event_has_its_own_id()
    {
        string source = Path.Combine(FindRepository(), "src");
        var seen = new Dictionary<int, string>();
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in EventId().Matches(File.ReadAllText(file)))
            {
                int id = int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
                string owner = $"{Path.GetFileName(file)}:{match.Groups["name"].Value}";
                Assert.False(seen.TryGetValue(id, out string? first), $"Event {id} is used by {first} and {owner}");
                seen[id] = owner;
            }
        }

        Assert.True(seen.Count > 50);
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Niskala.sln")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("Niskala.sln was not found above the test folder.");
    }

    [GeneratedRegex(@"\[LoggerMessage\(EventId = (?<id>\d+)[^\]]*\]\s*(?:public |internal |private )?static partial void (?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex EventId();
}
