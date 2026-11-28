using System.Reflection;

namespace Niskala.Updater.Tests;

public sealed class PublisherIdentityTests
{
    private const string Publisher = "Niskala Project";
    private const string Copyright = "Copyright (c) 2026 Niskala Project";
    private const string Repository = "https://github.com/Adriansyah101/NiskalaUtilities";
    private const string FormerHandle = "Adriansyah101";

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([TestRoots.Repository(), .. parts]));

    [Fact]
    public void The_build_props_name_the_project_as_author_company_and_copyright_holder()
    {
        string props = Read("Directory.Build.props");
        Assert.Contains($"<Authors>{Publisher}</Authors>", props, StringComparison.Ordinal);
        Assert.Contains($"<Company>{Publisher}</Company>", props, StringComparison.Ordinal);
        Assert.Contains($"<Copyright>{Copyright}</Copyright>", props, StringComparison.Ordinal);
        Assert.Contains("<PackageLicenseExpression>GPL-3.0-only</PackageLicenseExpression>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void The_compiled_assemblies_carry_the_project_identity()
    {
        Assembly assembly = typeof(GitHubReleaseClient).Assembly;
        Assert.Equal(Publisher, assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
        Assert.Equal(Copyright, assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
        Assert.Equal("Niskala Utilities", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Theory]
    [InlineData("native", "Niskala.Launcher", "src", "Launcher.rc")]
    [InlineData("native", "Niskala.EtwHelper", "src", "EtwHelper.rc")]
    public void Both_native_binaries_carry_the_project_identity(params string[] path)
    {
        string resource = Read(path);
        Assert.Contains($"VALUE \"CompanyName\", \"{Publisher}\"", resource, StringComparison.Ordinal);
        Assert.Contains($"VALUE \"LegalCopyright\", \"{Copyright}\"", resource, StringComparison.Ordinal);
        Assert.Contains("FILEVERSION NISKALA_VERSION_NUMBERS", resource, StringComparison.Ordinal);
        Assert.Contains("NISKALA_TEXT(NISKALA_VERSION)", resource, StringComparison.Ordinal);
    }

    [Fact]
    public void The_helper_dll_compiles_its_version_resource_from_the_shared_version()
    {
        string project = Read("native", "Niskala.EtwHelper", "Niskala.EtwHelper.vcxproj");
        Assert.Contains("<ResourceCompile Include=\"src\\EtwHelper.rc\" />", project, StringComparison.Ordinal);
        Assert.Contains("NISKALA_VERSION=$(Version);NISKALA_VERSION_NUMBERS=$(Version.Replace('.', ',')),0", project, StringComparison.Ordinal);
        string tracker = Read("src", "Niskala.Tracker", "Niskala.Tracker.csproj");
        Assert.Contains("$(EtwHelperDirectory)**\\*.rc", tracker, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props", tracker, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_is_published_by_the_project_but_links_to_the_same_repository()
    {
        string script = Read("installer", "NiskalaUtilities.iss");
        Assert.Contains($"AppPublisher={Publisher}\r\n", script, StringComparison.Ordinal);
        Assert.Contains($"VersionInfoCompany={Publisher}\r\n", script, StringComparison.Ordinal);
        Assert.Contains($"AppCopyright={Copyright}\r\n", script, StringComparison.Ordinal);
        Assert.Contains($"VersionInfoCopyright={Copyright}\r\n", script, StringComparison.Ordinal);
        Assert.Contains($"AppPublisherURL={Repository}\r\n", script, StringComparison.Ordinal);
        Assert.Contains($"AppUpdatesURL={Repository}/releases\r\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_swaps_the_previously_trusted_certificate_for_the_new_one()
    {
        string script = Read("installer", "NiskalaUtilities.iss");
        int remember = script.IndexOf("procedure RememberTrust(Store: string);", StringComparison.Ordinal);
        int end = script.IndexOf("#endif", remember, StringComparison.Ordinal);
        string body = script[remember..end];

        Assert.Contains("RegQueryStringValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store, Previous)", body, StringComparison.Ordinal);
        Assert.Contains("CompareText(Previous, PublisherThumbprint) <> 0", body, StringComparison.Ordinal);
        Assert.Contains("CertUtil('-delstore ' + Store + ' ' + Previous)", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("-delstore", StringComparison.Ordinal) < body.IndexOf("RegWriteStringValue", StringComparison.Ordinal),
            "The old certificate must be removed before the new thumbprint is remembered.");
        Assert.True(
            body.IndexOf("if not CertUtil('-store '", StringComparison.Ordinal) < body.IndexOf("-delstore", StringComparison.Ordinal),
            "The old certificate may only go once the new one is in the store.");
    }

    [Fact]
    public void Local_signing_uses_the_project_certificate_name()
    {
        string sign = Read("scripts", "Sign-Release.ps1");
        Assert.Contains($"[string]$Subject = 'CN={Publisher}',", sign, StringComparison.Ordinal);
        Assert.Contains($"$projectUrl = '{Repository}'", sign, StringComparison.Ordinal);
        Assert.DoesNotContain("-Subject", Read("scripts", "Build-Release.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_names_the_new_certificate_and_keeps_its_release_links()
    {
        string readme = Read("README.md");
        Assert.Contains($"(`CN={Publisher}`)", readme, StringComparison.Ordinal);
        Assert.Contains($"\r\n{Copyright}\r\n", readme, StringComparison.Ordinal);
        Assert.Contains($"{Repository}/releases/latest", readme, StringComparison.Ordinal);
        Assert.DoesNotContain($"CN={FormerHandle}", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_updater_still_reads_releases_from_the_same_repository()
    {
        Assert.Equal("https://api.github.com/repos/Adriansyah101/NiskalaUtilities/releases/latest", GitHubReleaseClient.LatestRelease.AbsoluteUri);
    }

    [Fact]
    public void No_shipped_file_names_the_former_publisher_outside_a_repository_link()
    {
        string root = TestRoots.Repository();
        string[] folders = ["src", "native", "installer", "scripts"];
        string[] extensions = [".cs", ".xaml", ".cpp", ".h", ".rc", ".ps1", ".iss", ".csproj", ".vcxproj", ".props", ".manifest", ".md"];
        IEnumerable<string> files = folders
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "Directory.Build.props"))
            .Append(Path.Combine(root, "README.md"))
            .Append(Path.Combine(root, "PATCHNOTES.md"))
            .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file).Replace($"/{FormerHandle}/NiskalaUtilities", "/NiskalaUtilities", StringComparison.Ordinal);
            Assert.False(text.Contains(FormerHandle, StringComparison.Ordinal), $"{Path.GetRelativePath(root, file)} still names {FormerHandle} outside a link.");
        }
    }
}
