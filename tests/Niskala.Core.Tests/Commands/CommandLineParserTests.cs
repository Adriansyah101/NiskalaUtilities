using Niskala.Core.Commands;

namespace Niskala.Core.Tests.Commands;

public sealed class CommandLineParserTests
{
    private sealed class FakeEnvironment(params string[] files) : ICommandEnvironment
    {
        private readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => _files.Contains(path);

        public string SystemDirectory => @"C:\Windows\System32";

        public string WindowsDirectory => @"C:\Windows";

        public string ExpandVariables(string text) =>
            text.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quoted_executable_with_arguments()
    {
        var env = new FakeEnvironment(@"C:\Program Files\Acme\unins000.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"""C:\Program Files\Acme\unins000.exe"" /SILENT", env);

        Assert.Equal(@"C:\Program Files\Acme\unins000.exe", parsed.Executable);
        Assert.Equal("/SILENT", parsed.Arguments);
        Assert.True(parsed.ExecutableExists);
        Assert.Equal(CommandParseIssue.None, parsed.Issue);
    }

    [Fact]
    public void Unquoted_path_with_spaces_is_split_at_the_real_executable()
    {
        var env = new FakeEnvironment(@"C:\Program Files\Acme Suite\uninst.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"C:\Program Files\Acme Suite\uninst.exe /S", env);

        Assert.Equal(@"C:\Program Files\Acme Suite\uninst.exe", parsed.Executable);
        Assert.Equal("/S", parsed.Arguments);
        Assert.False(parsed.IsAmbiguous);
    }

    [Fact]
    public void Planted_shorter_prefix_is_flagged_and_not_preferred()
    {
        var env = new FakeEnvironment(@"C:\Program.exe", @"C:\Program Files\Acme\uninst.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"C:\Program Files\Acme\uninst.exe /S", env);

        Assert.Equal(@"C:\Program Files\Acme\uninst.exe", parsed.Executable);
        Assert.True(parsed.IsAmbiguous);
    }

    [Fact]
    public void Bare_system_tool_is_resolved_in_System32_only()
    {
        var env = new FakeEnvironment(@"C:\Windows\System32\MsiExec.exe");
        ParsedCommand parsed = CommandLineParser.Parse("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", env);

        Assert.Equal(@"C:\Windows\System32\MsiExec.exe", parsed.Executable);
        Assert.Equal("/X{11111111-2222-3333-4444-555555555555}", parsed.Arguments);
    }

    [Fact]
    public void Bare_name_without_extension_gets_exe_appended()
    {
        var env = new FakeEnvironment(@"C:\Windows\System32\rundll32.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"rundll32 C:\Acme\x.dll,Uninstall", env);

        Assert.Equal(@"C:\Windows\System32\rundll32.exe", parsed.Executable);
        Assert.Equal(@"C:\Acme\x.dll,Uninstall", parsed.Arguments);
    }

    [Fact]
    public void Bare_name_outside_system_folders_is_not_resolved()
    {
        var env = new FakeEnvironment(@"C:\Users\bob\AppData\Local\Microsoft\WindowsApps\winget.exe");
        ParsedCommand parsed = CommandLineParser.Parse("winget uninstall --product-code Acme", env);

        Assert.Equal("winget", parsed.Executable);
        Assert.False(parsed.ExecutableExists);
        Assert.Equal(CommandParseIssue.ExecutableNotFound, parsed.Issue);
    }

    [Fact]
    public void Relative_path_with_folder_is_never_resolved()
    {
        var env = new FakeEnvironment(@"C:\Windows\System32\tools\x.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"tools\x.exe /u", env);

        Assert.False(parsed.ExecutableExists);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var env = new FakeEnvironment(@"C:\Program Files\Acme\uninstall.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"""%ProgramFiles%\Acme\uninstall.exe"" /quiet", env);

        Assert.Equal(@"C:\Program Files\Acme\uninstall.exe", parsed.Executable);
        Assert.Equal(@"""%ProgramFiles%\Acme\uninstall.exe"" /quiet", parsed.Raw);
    }

    [Fact]
    public void Missing_executable_is_reported()
    {
        ParsedCommand parsed = CommandLineParser.Parse(@"""C:\Gone\uninst.exe"" /S", new FakeEnvironment());

        Assert.Equal(@"C:\Gone\uninst.exe", parsed.Executable);
        Assert.Equal(CommandParseIssue.ExecutableNotFound, parsed.Issue);
    }

    [Fact]
    public void Unterminated_quote_is_reported()
    {
        var env = new FakeEnvironment(@"C:\Acme\uninst.exe");
        ParsedCommand parsed = CommandLineParser.Parse(@"""C:\Acme\uninst.exe", env);

        Assert.Equal(CommandParseIssue.UnterminatedQuote, parsed.Issue);
        Assert.Equal(@"C:\Acme\uninst.exe", parsed.Executable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_command(string? raw)
    {
        Assert.Equal(CommandParseIssue.Empty, CommandLineParser.Parse(raw, new FakeEnvironment()).Issue);
    }
}
