using Niskala.Core.Safety;

namespace Niskala.Core.Tests.Safety;

public sealed class Win32PathResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _protected;
    private readonly PathGuard _guard;

    public Win32PathResolverTests()
    {
        _protected = _temp.CreateDirectory("Protected Long Folder Name");
        _temp.CreateDirectory(@"Protected Long Folder Name\inner");
        _guard = new PathGuard(
            ProtectedLocations.Create([new ProtectedLocation(_protected, ProtectionKind.Tree, "test")], Win32PathResolver.Instance),
            Win32PathResolver.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Path_through_a_junction_is_denied_by_the_target_rule()
    {
        string link = _temp.CreateJunction("innocent-link", _protected);

        PathDecision decision = _guard.Check(Path.Combine(link, "inner"));

        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
        Assert.Equal(Normalize(Path.Combine(_protected, "inner")), decision.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void Missing_path_through_a_junction_is_denied_too()
    {
        string link = _temp.CreateJunction("innocent-link", _protected);

        PathDecision decision = _guard.Check(Path.Combine(link, "not-created-yet", "file.txt"));

        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
        Assert.False(decision.Exists);
    }

    [Fact]
    public void The_junction_itself_resolves_to_its_own_location()
    {
        string link = _temp.CreateJunction("innocent-link", _protected);

        PathDecision decision = _guard.Check(link);

        Assert.True(decision.IsAllowed, decision.ToString());
        Assert.True(decision.IsReparsePoint);
        Assert.True(decision.IsNameSurrogate);
        Assert.Equal(Normalize(link), decision.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void Junction_into_the_Windows_folder_is_denied_by_the_machine_rules()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string link = _temp.CreateJunction("win-link", windows);
        PathGuard machine = PathGuard.ForCurrentMachine();

        PathDecision decision = machine.Check(Path.Combine(link, "System32"));

        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
    }

    [Fact]
    public void Short_8dot3_name_is_expanded_before_the_rules_run()
    {
        string inner = Path.Combine(_protected, "inner");
        string shortPath = ShortPaths.ToShort(inner);
        if (string.Equals(shortPath, inner, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("8.3 names are disabled on this volume.");
        }

        PathDecision decision = _guard.Check(shortPath);

        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
        Assert.Equal(Normalize(inner), decision.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void Missing_segment_with_a_tilde_cannot_be_verified()
    {
        PathDecision decision = _guard.Check(Path.Combine(_temp.Root, "NOTHER~1", "x"));
        Assert.Equal(PathVerdict.UnresolvedShortName, decision.Verdict);
    }

    [Fact]
    public void Missing_path_under_an_existing_folder_resolves()
    {
        PathDecision decision = _guard.Check(Path.Combine(_temp.Root, "missing", "deeper"));

        Assert.True(decision.IsAllowed, decision.ToString());
        Assert.False(decision.Exists);
    }

    [Fact]
    public void Drive_letter_that_does_not_exist_is_denied()
    {
        string? free = Enumerable.Range('D', 23)
            .Select(c => $"{(char)c}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        if (free is null)
        {
            Assert.Skip("No free drive letter.");
        }

        Assert.Equal(PathVerdict.MissingDrive, _guard.Check(free + "Acme").Verdict);
    }

    private static string Normalize(string path)
    {
        Assert.Equal(PathVerdict.Allowed, PathSyntax.TryNormalize(path, out string normalized));

        return ShortPaths.ToLong(normalized);
    }
}
