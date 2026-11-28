using System.Text.RegularExpressions;

namespace Niskala.Updater.Tests;

public sealed class InstallerScriptTests
{
    private const string InstallerAppId = "{3EC397E4-E199-4B2F-B436-8F7D0FC248B9}";

    [Fact]
    public void The_installer_keeps_its_uninstall_key_so_updates_replace_the_install()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        Assert.Contains("AppId={" + InstallerAppId, script, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\[Setup\]\r?$", script);
        Assert.DoesNotMatch(@"(?m)^\s*;", script);
        Assert.DoesNotMatch(@"(?m)^\s*Uninstallable\s*=\s*no", script);
    }

    [Fact]
    public void The_installer_trusts_the_publisher_machine_wide_and_forgets_only_what_it_added()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        foreach (string store in new[] { "Root", "TrustedPublisher" })
        {
            string line = Assert.Single(Lines(script), l => l.Contains($"-f -addstore {store} ", StringComparison.Ordinal));
            Assert.Contains($"-addstore {store} \"\"{{tmp}}\\{{#CertificateFile}}\"\"\"", line, StringComparison.Ordinal);
            Assert.Contains($"Check: NeedsTrust('{store}'); AfterInstall: RememberTrust('{store}')", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Tasks:", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Components:", line, StringComparison.Ordinal);
            Assert.Contains($"ForgetTrust('{store}');", script, StringComparison.Ordinal);
        }

        Assert.Contains("#define CertificateFile \"niskala-pub.cer\"", script, StringComparison.Ordinal);
        Assert.Contains("RegWriteStringValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store", script, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store);", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("-addstore TrustedPublisher", StringComparison.Ordinal) < script.IndexOf("Check: RestartAfterUpdate", StringComparison.Ordinal),
            "The certificate must be trusted before an update starts the app again.");
    }

    [Fact]
    public void The_certificate_is_trusted_on_every_install_without_a_task()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        string tasks = Section(script, "Tasks");
        Assert.DoesNotContain("trust", tasks, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", tasks, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Adriansyah101", tasks, StringComparison.Ordinal);
        Assert.DoesNotContain("trustpublisher", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_certificate_is_unpacked_to_the_setup_temporary_folder_and_never_to_the_app_folder()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        string entry = Assert.Single(Lines(Section(script, "Files")), l => l.Contains("{#PublisherCertificate}", StringComparison.Ordinal));
        Assert.Contains("DestDir: \"{tmp}\"", entry, StringComparison.Ordinal);
        Assert.Contains("DestName: \"{#CertificateFile}\"", entry, StringComparison.Ordinal);
        Assert.Contains("deleteafterinstall", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("{app}\\{#CertificateFile}", Section(script, "Files") + Section(script, "Run"), StringComparison.Ordinal);
        Assert.Contains("Type: files; Name: \"{app}\\{#CertificateFile}\"", Section(script, "InstallDelete"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_scripts_stage_the_certificate_outside_the_release_folder_and_delete_it()
    {
        string scripts = Path.Combine(TestRoots.Repository(), "scripts");
        string build = File.ReadAllText(Path.Combine(scripts, "Build-Release.ps1"));
        string sign = File.ReadAllText(Path.Combine(scripts, "Sign-Release.ps1"));

        Assert.Contains("$certificateStaging = Join-Path $staging 'publisher'", build, StringComparison.Ordinal);
        Assert.Contains("$publisherCertificate = Join-Path $certificateStaging 'niskala-pub.cer'", build, StringComparison.Ordinal);
        Assert.Contains("$staging = Join-Path $repo 'artifacts\\staging'", build, StringComparison.Ordinal);
        Assert.Matches(@"finally \{\r?\n\s+Clear-Folder \$certificateStaging", build);
        Assert.DoesNotContain("$packages += $signing.Certificate", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $release 'niskala-pub.cer'", build, StringComparison.Ordinal);
        Assert.Contains("holds certificate files", build, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts\\release", sign, StringComparison.OrdinalIgnoreCase);
        int cleared = build.IndexOf("finally {\r\n    Clear-Folder $certificateStaging", StringComparison.Ordinal);
        Assert.True(cleared > 0, "The staged certificate must be deleted in a finally block.");
        Assert.True(
            cleared < build.IndexOf("SHA256SUMS.txt", StringComparison.Ordinal),
            "The staged certificate must be gone before the checksums are written.");
    }

    [Fact]
    public void A_built_release_holds_only_its_assets_and_no_certificate()
    {
        string release = Path.Combine(TestRoots.Repository(), "artifacts", "release");
        if (!Directory.Exists(release))
        {
            return;
        }

        Assert.Empty(Directory.GetFiles(release, "*.cer", SearchOption.AllDirectories));
        string sums = Path.Combine(release, "SHA256SUMS.txt");
        if (!File.Exists(sums))
        {
            return;
        }

        string[] listed = [.. File.ReadAllLines(sums).Where(l => l.Length > 0).Select(l => l[(l.IndexOf("  ", StringComparison.Ordinal) + 2)..])];
        if (!listed.Any(n => n.Contains($"-v{ProjectVersion()}-", StringComparison.Ordinal)))
        {
            Assert.Skip("artifacts\\release holds a build of another version.");
        }

        Assert.DoesNotContain(listed, n => n.EndsWith(".cer", StringComparison.OrdinalIgnoreCase));
        string[] expected = [.. listed.Append("SHA256SUMS.txt").Order(StringComparer.OrdinalIgnoreCase)];
        string[] present = [.. Directory.GetFiles(release).Select(p => Path.GetFileName(p)).Order(StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(expected, present);
        Assert.Equal(ReleaseNames.Installer(ReleaseVersion.Parse(ProjectVersion()), "win-x64"), Assert.Single(listed));
        Assert.DoesNotContain(present, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetDirectories(release));
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Section(string script, string name)
    {
        int start = script.IndexOf($"[{name}]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The installer has no [{name}] section.");
        int end = script.IndexOf("\n[", start + name.Length + 2, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }

    [Fact]
    public void The_installer_output_matches_the_asset_the_updater_looks_for()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        string output = ReleaseNames.Installer(Releases.V011, "win-x64").Replace("0.1.1", "{#AppVersion}", StringComparison.Ordinal);
        Assert.Contains("OutputBaseFilename=" + Path.GetFileNameWithoutExtension(output).Replace("win-x64", "{#Runtime}", StringComparison.Ordinal), script, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=yes", script, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=yes", script, StringComparison.Ordinal);
        Assert.Contains(UpdateLauncher.RestartArguments.Split(' ')[^1], script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_script_builds_only_the_installer()
    {
        string build = File.ReadAllText(Path.Combine(TestRoots.Repository(), "scripts", "Build-Release.ps1"));
        Assert.DoesNotContain("Niskala.CLI", build, StringComparison.Ordinal);
        Assert.DoesNotContain("niskala.exe", build, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ReleaseArchive", build, StringComparison.Ordinal);
        Assert.DoesNotContain(".zip", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA256SUMS.txt", build, StringComparison.Ordinal);
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        Assert.DoesNotContain("CLI", script, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(TestRoots.Repository(), "scripts", "New-ReleaseArchive.cs")));
        Assert.False(Directory.Exists(Path.Combine(TestRoots.Repository(), "src", "Niskala.CLI")));
    }

    private static string ProjectVersion()
    {
        string props = File.ReadAllText(Path.Combine(TestRoots.Repository(), "Directory.Build.props"));
        return Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
    }
}
