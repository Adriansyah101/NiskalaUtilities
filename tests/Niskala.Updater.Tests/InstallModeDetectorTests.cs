namespace Niskala.Updater.Tests;

public sealed class InstallModeDetectorTests
{
    private sealed class Locations(params string[] locations) : IInstallLocationSource
    {
        public IReadOnlyList<string> InstallLocations() => locations;
    }

    private static string Layout(out string lib, out string cli)
    {
        string root = Directory.CreateDirectory(Path.Combine(TestRoots.NewFolder(), "Niskala Utilities")).FullName;
        lib = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
        cli = Directory.CreateDirectory(Path.Combine(root, "cli")).FullName;
        return root;
    }

    [Fact]
    public void The_uninstall_entry_marks_every_folder_below_it_as_installed()
    {
        string root = Layout(out string lib, out string cli);
        var detector = new InstallModeDetector(new Locations(root + Path.DirectorySeparatorChar));

        Assert.Equal(new InstallInfo(InstallMode.Installed, root), detector.Detect(lib));
        Assert.Equal(new InstallInfo(InstallMode.Installed, root), detector.Detect(cli + Path.DirectorySeparatorChar));
        Assert.True(detector.Detect(root).IsInstalled);
    }

    [Fact]
    public void An_uninstaller_with_its_data_file_marks_the_copy_as_installed()
    {
        string root = Layout(out string lib, out string cli);
        File.WriteAllText(Path.Combine(root, "unins000.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "unins000.dat"), string.Empty);
        var detector = new InstallModeDetector(new Locations());

        Assert.Equal(new InstallInfo(InstallMode.Installed, root), detector.Detect(lib));
        Assert.Equal(new InstallInfo(InstallMode.Installed, root), detector.Detect(cli));
    }

    [Fact]
    public void An_uninstaller_without_its_data_file_is_not_enough()
    {
        string root = Layout(out _, out string cli);
        File.WriteAllText(Path.Combine(root, "unins000.exe"), string.Empty);

        Assert.Equal(new InstallInfo(InstallMode.Portable, cli), new InstallModeDetector(new Locations()).Detect(cli));
    }

    [Fact]
    public void A_portable_desktop_copy_reports_the_folder_above_lib()
    {
        string root = Layout(out string lib, out _);
        Assert.Equal(new InstallInfo(InstallMode.Portable, root), new InstallModeDetector(new Locations()).Detect(lib + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_copy_outside_the_registered_folder_is_portable()
    {
        string root = Layout(out string lib, out _);
        string sibling = Directory.CreateDirectory(root + " 2").FullName;
        var detector = new InstallModeDetector(new Locations(Path.Combine(TestRoots.Folder, "elsewhere"), root));

        Assert.Equal(InstallMode.Portable, detector.Detect(sibling).Mode);
        Assert.Equal(InstallMode.Installed, detector.Detect(lib).Mode);
    }

    [Fact]
    public void The_installer_and_the_detector_agree_on_the_uninstall_key()
    {
        string script = File.ReadAllText(Path.Combine(TestRoots.Repository(), "installer", "NiskalaUtilities.iss"));
        Assert.Contains("AppId={" + RegistryInstallLocations.AppId, script, StringComparison.Ordinal);
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
        Assert.DoesNotContain(listed, n => n.EndsWith(".cer", StringComparison.OrdinalIgnoreCase));
        string[] expected = [.. listed.Append("SHA256SUMS.txt").Order(StringComparer.OrdinalIgnoreCase)];
        string[] present = [.. Directory.GetFiles(release).Select(p => Path.GetFileName(p)).Order(StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(expected, present);
        if (listed.Any(n => n.Contains("-Setup-", StringComparison.Ordinal)))
        {
            Assert.Equal(3, listed.Length);
        }
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
}
