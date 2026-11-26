using Niskala.Core.Safety;

namespace Niskala.Core.Tests.Safety;

public sealed class PathGuardRuleTests
{
    private static PathGuard Guard(FakeResolver resolver, params ProtectedLocation[] rules) =>
        new(ProtectedLocations.Create(rules, new FakeResolver()), resolver);

    private static ProtectedLocation Tree(string path) => new(path, ProtectionKind.Tree, "test tree");

    private static ProtectedLocation Self(string path) => new(path, ProtectionKind.Self, "test self");

    [Fact]
    public void ProtectTree_denies_the_folder_and_everything_inside()
    {
        PathGuard guard = Guard(new FakeResolver(), Tree(@"Z:\Windows"));

        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\Windows").Verdict);
        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\Windows\System32\drivers\etc\hosts").Verdict);
        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"z:\WINDOWS\system32").Verdict);
        Assert.True(guard.Check(@"Z:\WindowsTools-lookalike").IsAllowed);
    }

    [Fact]
    public void ProtectSelf_denies_the_folder_but_allows_its_children()
    {
        PathGuard guard = Guard(new FakeResolver(), Self(@"Z:\Program Files"));

        Assert.Equal(PathVerdict.ProtectedSelf, guard.Check(@"Z:\Program Files").Verdict);
        Assert.True(guard.Check(@"Z:\Program Files\Acme").IsAllowed);
        Assert.True(guard.Check(@"Z:\Program Files\Acme\bin\acme.exe").IsAllowed);
    }

    [Fact]
    public void Deepest_rule_wins()
    {
        PathGuard guard = Guard(
            new FakeResolver(),
            Tree(@"Z:\ProgramData\Microsoft"),
            Self(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs"),
            Tree(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Administrative Tools"));

        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\ProgramData\Microsoft\Crypto").Verdict);
        Assert.Equal(PathVerdict.ProtectedSelf, guard.Check(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs").Verdict);
        Assert.True(guard.Check(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme").IsAllowed);
        Assert.Equal(
            PathVerdict.ProtectedTree,
            guard.Check(@"Z:\ProgramData\Microsoft\Windows\Start Menu\Programs\Administrative Tools\x.lnk").Verdict);
    }

    [Fact]
    public void Tree_beats_Self_at_the_same_depth()
    {
        PathGuard guard = Guard(new FakeResolver(), Self(@"Z:\Data"), Tree(@"Z:\Data"));
        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\Data\x").Verdict);
    }

    [Fact]
    public void A_parent_of_a_protected_location_is_denied()
    {
        PathGuard guard = Guard(new FakeResolver(), Self(@"Z:\Users\bob\AppData\Roaming"));

        PathDecision decision = guard.Check(@"Z:\Users\bob");
        Assert.Equal(PathVerdict.ContainsProtected, decision.Verdict);
        Assert.Equal(@"Z:\Users\bob\AppData\Roaming", decision.MatchedRule?.Path);
        Assert.Equal(PathVerdict.ContainsProtected, guard.Check(@"Z:\Users").Verdict);
    }

    [Fact]
    public void Drive_roots_are_always_denied()
    {
        PathGuard guard = Guard(new FakeResolver());
        Assert.Equal(PathVerdict.DriveRoot, guard.Check(@"Z:\").Verdict);
        Assert.Equal(PathVerdict.DriveRoot, guard.Check(@"\\?\Z:\").Verdict);
    }

    [Fact]
    public void Child_name_prefix_rule_protects_matching_children_only()
    {
        PathGuard guard = Guard(
            new FakeResolver(),
            Self(@"Z:\Users\bob"),
            new ProtectedLocation(@"Z:\Users\bob", ProtectionKind.Tree, "hive", ChildNamePrefix: "ntuser."));

        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\Users\bob\NTUSER.DAT").Verdict);
        Assert.Equal(PathVerdict.ProtectedTree, guard.Check(@"Z:\Users\bob\ntuser.dat.LOG1").Verdict);
        Assert.True(guard.Check(@"Z:\Users\bob\acme-settings.ini").IsAllowed);
        Assert.True(guard.Check(@"Z:\Users\bob\.acme").IsAllowed);
    }

    [Fact]
    public void Junction_into_a_protected_tree_is_caught_on_the_physical_path()
    {
        var resolver = new FakeResolver().Link(@"Z:\Temp\innocent", @"Z:\Windows");
        PathGuard guard = Guard(resolver, Tree(@"Z:\Windows"));

        PathDecision decision = guard.Check(@"Z:\Temp\innocent\System32");
        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
        Assert.Equal(@"Z:\Windows\System32", decision.CanonicalPath);
    }

    [Fact]
    public void The_link_itself_may_be_removed_but_must_not_be_traversed()
    {
        var resolver = new FakeResolver().Link(@"Z:\Temp\innocent", @"Z:\Windows");
        PathGuard guard = Guard(resolver, Tree(@"Z:\Windows"));

        PathDecision decision = guard.Check(@"Z:\Temp\innocent");
        Assert.True(decision.IsAllowed);
        Assert.True(decision.IsNameSurrogate);
    }

    [Fact]
    public void Lexical_match_denies_before_any_resolution()
    {
        var resolver = new FakeResolver().Link(@"Z:\Windows", @"Z:\Elsewhere");
        PathGuard guard = Guard(resolver, Tree(@"Z:\Windows"));

        PathDecision decision = guard.Check(@"Z:\Windows\x");
        Assert.Equal(PathVerdict.ProtectedTree, decision.Verdict);
        Assert.Null(decision.CanonicalPath);
    }

    [Fact]
    public void Rules_with_invalid_paths_are_skipped_not_fatal()
    {
        ProtectedLocations locations = ProtectedLocations.Create(
            [Tree(@"relative\path"), Tree(@"Z:\Valid")],
            new FakeResolver());

        ProtectedLocation rule = Assert.Single(locations.Rules);
        Assert.Equal(@"Z:\Valid", rule.Path);
    }

    [Fact]
    public void Rules_are_added_in_both_spellings_when_they_resolve_elsewhere()
    {
        var resolver = new FakeResolver().Link(@"Z:\Users", @"Y:\Users");
        ProtectedLocations locations = ProtectedLocations.Create([Self(@"Z:\Users\bob")], resolver);
        var guard = new PathGuard(locations, new FakeResolver());

        Assert.Equal(PathVerdict.ProtectedSelf, guard.Check(@"Y:\Users\bob").Verdict);
        Assert.Equal(PathVerdict.ProtectedSelf, guard.Check(@"Z:\Users\bob").Verdict);
    }
}
