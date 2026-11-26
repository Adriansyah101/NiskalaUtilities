using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;

namespace Niskala.Tracker.Tests;

public sealed class NoiseFilterTests
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static readonly NoiseFilter Filter = NoiseFilter.ForCurrentMachine(PathGuard.ForCurrentMachine(), RegistryGuard.ForCurrentMachine(), SystemCommandEnvironment.Instance);

    [Fact]
    public void Caches_temporary_files_and_protected_folders_are_left_out()
    {
        FileChange[] files =
        [
            new($@"{ProgramFiles}\Acme Test", ChangeKind.Created, true),
            new($@"{ProgramFiles}\Acme Test\acme.exe", ChangeKind.Created, false),
            new(Path.Combine(Path.GetTempPath(), "setup-1234.tmp"), ChangeKind.Created, false),
            new($@"{LocalAppData}\Microsoft\Windows\INetCache\IE\x.dat", ChangeKind.Created, false),
            new($@"{LocalAppData}\Google\Chrome\User Data\Profile 2\Code Cache\js\a", ChangeKind.Created, false),
            new($@"{LocalAppData}\Google\Chrome\User Data\Profile 2\Preferences", ChangeKind.Modified, false),
            new($@"{Windows}\System32\acme-driver.sys", ChangeKind.Created, false),
            new($@"{Windows}\Prefetch\SETUP.EXE-1234.pf", ChangeKind.Created, false),
        ];

        FilteredChanges result = Filter.Apply(new ReconciledChanges(files, [], []));

        Assert.Equal(
            [$@"{ProgramFiles}\Acme Test", $@"{ProgramFiles}\Acme Test\acme.exe", $@"{LocalAppData}\Google\Chrome\User Data\Profile 2\Preferences"],
            result.Changes.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, result.Filtered["Temporary files"]);
        Assert.Equal(1, result.Filtered["Internet cache"]);
        Assert.Equal(1, result.Filtered["Browser cache"]);
        Assert.Equal(2, result.Filtered.Where(f => f.Key.StartsWith("Protected", StringComparison.Ordinal) || f.Key == "Prefetch").Sum(f => f.Value));
    }

    [Fact]
    public void Contents_of_a_left_out_folder_go_with_it()
    {
        string cache = $@"{LocalAppData}\D3DSCache\acme";
        FileChange[] files = [new(cache, ChangeKind.Created, true), new($@"{cache}\a.bin", ChangeKind.Created, false), new($@"{cache}\b.bin", ChangeKind.Created, false)];

        FilteredChanges result = Filter.Apply(new ReconciledChanges(files, [], []));

        Assert.Empty(result.Changes.Files);
        Assert.Equal(3, result.Filtered["Shader cache"]);
    }

    [Fact]
    public void Shell_state_and_guarded_keys_are_left_out_and_the_app_keys_stay()
    {
        KeyChange[] keys =
        [
            new(@"HKCU\Software\Acme Test", ChangeKind.Created),
            new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count", ChangeKind.Created),
            new(@"HKLM\SOFTWARE\Microsoft\Tracing\setup_RASAPI32", ChangeKind.Created),
            new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\ABC", ChangeKind.Created),
            new(@"HKLM\SOFTWARE\Microsoft", ChangeKind.Created),
        ];

        ValueChange[] values =
        [
            new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Acme Test", ChangeKind.Created),
            new(@"HKLM\SOFTWARE\Microsoft\Cryptography\RNG", "Seed", ChangeKind.Modified),
        ];

        FilteredChanges result = Filter.Apply(new ReconciledChanges([], keys, values));

        Assert.Equal(@"HKCU\Software\Acme Test", Assert.Single(result.Changes.Keys).Path);
        Assert.Equal("Acme Test", Assert.Single(result.Changes.Values).Name);
        Assert.Equal(1, result.Filtered["Shell state"]);
        Assert.Equal(1, result.Filtered["Network tracing"]);
        Assert.Equal(1, result.Filtered["Windows Installer bookkeeping"]);
        Assert.Equal(1, result.Filtered["Cryptography"]);
        Assert.Contains(result.Filtered.Keys, k => k.StartsWith("Protected", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Google\Chrome\User Data\*\Cache", @"C:\Users\a\AppData\Local\Google\Chrome\User Data\Default\Cache\f_0001", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Google\Chrome\User Data\*\Cache", @"C:\Users\a\AppData\Local\Google\Chrome\User Data\Cache", false)]
    [InlineData(@"HKCU\Software\Microsoft\Tracing", @"hkcu\software\microsoft\tracing\x", true)]
    [InlineData(@"HKCU\Software\Microsoft\Tracing", @"HKCU\Software\Microsoft\TracingTool", false)]
    public void Rules_match_whole_segments(string pattern, string path, bool matches) =>
        Assert.Equal(matches, new NoiseRule(pattern, "test").Matches(path));
}
