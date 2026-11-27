namespace Niskala.Repair.Tests;

public sealed class RepairSourceTests
{
    private static readonly IRepairEnvironment Machine = new Environment64();

    [Theory]
    [InlineData(@"D:\sources\install.wim", "/Source:WIM:D:\\sources\\install.wim:1", RepairSourceKind.Wim)]
    [InlineData(@"E:\sources\INSTALL.ESD", "/Source:ESD:E:\\sources\\INSTALL.ESD:1", RepairSourceKind.Esd)]
    [InlineData(@"  C:\Images\Windows 11\install.wim  ", "/Source:WIM:C:\\Images\\Windows 11\\install.wim:1", RepairSourceKind.Wim)]
    public void A_local_image_becomes_a_source_argument(string path, string argument, RepairSourceKind kind)
    {
        Assert.True(RepairSource.TryCreate(path, RepairSource.DefaultIndex, _ => FileAttributes.Archive, out RepairSource? source, out RepairSourceProblem problem));

        Assert.Equal(RepairSourceProblem.None, problem);
        Assert.Equal(kind, source.Kind);
        Assert.Equal(argument, source.Argument);
        Assert.True(RepairSource.IsArgument(source.Argument));
    }

    [Theory]
    [InlineData("", RepairSourceProblem.NotFullPath)]
    [InlineData(@"sources\install.wim", RepairSourceProblem.NotFullPath)]
    [InlineData(@"\\server\share\install.wim", RepairSourceProblem.NotFullPath)]
    [InlineData(@"D:\sources\boot.iso", RepairSourceProblem.WrongExtension)]
    [InlineData(@"D:\sources\install.swm", RepairSourceProblem.WrongExtension)]
    [InlineData("D:\\sources\\in\"stall.wim", RepairSourceProblem.UnsafeCharacters)]
    [InlineData(@"D:\sources\install.wim:2", RepairSourceProblem.UnsafeCharacters)]
    [InlineData(@"D:\sour:ces\install.wim", RepairSourceProblem.UnsafeCharacters)]
    public void Paths_dism_should_not_get_are_refused(string path, RepairSourceProblem expected)
    {
        Assert.False(RepairSource.TryCreate(path, RepairSource.DefaultIndex, _ => FileAttributes.Archive, out RepairSource? source, out RepairSourceProblem problem));

        Assert.Null(source);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void Missing_files_folders_and_links_are_refused()
    {
        Assert.False(RepairSource.TryCreate(@"D:\sources\install.wim", 1, _ => throw new FileNotFoundException(), out _, out RepairSourceProblem missing));
        Assert.False(RepairSource.TryCreate(@"D:\sources\install.wim", 1, _ => FileAttributes.Directory, out _, out RepairSourceProblem folder));
        Assert.False(RepairSource.TryCreate(@"D:\sources\install.wim", 1, _ => FileAttributes.ReparsePoint | FileAttributes.Archive, out _, out RepairSourceProblem link));

        Assert.Equal(RepairSourceProblem.Missing, missing);
        Assert.Equal(RepairSourceProblem.Missing, folder);
        Assert.Equal(RepairSourceProblem.Link, link);
    }

    [Fact]
    public void The_component_store_repair_runs_offline_from_the_chosen_image()
    {
        RepairSource source = Source(@"D:\sources\install.esd");

        RepairInvocation invocation = RepairCommandCatalog.Resolve(RepairCommandId.DismRestoreHealth, Machine, source);

        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth", "/Source:ESD:D:\\sources\\install.esd:1", "/LimitAccess"], invocation.Arguments);
        Assert.Equal(@"Dism.exe /Online /Cleanup-Image /RestoreHealth /Source:ESD:D:\sources\install.esd:1 /LimitAccess", invocation.CommandLine);
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth"], RepairCommandCatalog.Resolve(RepairCommandId.DismRestoreHealth, Machine).Arguments);
    }

    [Fact]
    public void A_path_with_spaces_is_quoted_in_the_shown_command_line()
    {
        RepairSource source = Source(@"C:\Windows Images\install.wim");

        Assert.EndsWith("\"/Source:WIM:C:\\Windows Images\\install.wim:1\" /LimitAccess", RepairCommandCatalog.Resolve(RepairCommandId.DismRestoreHealth, Machine, source).CommandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepairCommandId.DismCheckHealth)]
    [InlineData(RepairCommandId.SfcScanNow)]
    [InlineData(RepairCommandId.DismComponentCleanup)]
    [InlineData(RepairCommandId.NetshWinsockReset)]
    [InlineData(RepairCommandId.FlushDns)]
    public void Only_the_component_store_repair_takes_a_source(RepairCommandId id)
    {
        Assert.False(RepairCommandCatalog.AcceptsSource(id));
        Assert.Throws<ArgumentException>(() => RepairCommandCatalog.Resolve(id, Machine, Source(@"D:\sources\install.wim")));
    }

    [Theory]
    [InlineData("/Source:WIM:D:\\sources\\install.wim:1", true)]
    [InlineData("/LimitAccess", true)]
    [InlineData("/Source:WIM:D:\\sources\\install.esd:1", false)]
    [InlineData("/Source:WIM:D:\\sources\\install.wim:0", false)]
    [InlineData("/Source:WIM:D:\\sources\\install.wim", false)]
    [InlineData("/Source:VHD:D:\\sources\\install.wim:1", false)]
    [InlineData("/Source:WIM:\\\\server\\share\\install.wim:1", false)]
    [InlineData("/Source:C:\\Windows\\WinSxS", false)]
    public void Only_well_formed_image_sources_pass_the_argument_check(string argument, bool expected)
    {
        Assert.Equal(expected, RepairSource.IsArgument(argument));
    }

    [Theory]
    [InlineData(unchecked((int)0x800F0915), RepairDiagnostic.RepairSourceNotFound)]
    [InlineData(unchecked((int)0x800F081F), RepairDiagnostic.SourceFilesMissing)]
    [InlineData(unchecked((int)0x800F0906), RepairDiagnostic.SourceDownloadFailed)]
    public void Missing_repair_files_point_to_a_local_source(int exitCode, RepairDiagnostic diagnostic)
    {
        (RepairOutcome outcome, RepairDiagnostic found) = RepairExitCodes.Interpret(RepairCommandCatalog.Get(RepairCommandId.DismRestoreHealth), exitCode);

        Assert.Equal(RepairOutcome.Failed, outcome);
        Assert.Equal(diagnostic, found);
        Assert.True(RepairExitCodes.NeedsLocalSource(found));
        Assert.False(RepairExitCodes.NeedsLocalSource(RepairDiagnostic.AccessDenied));
        Assert.False(RepairExitCodes.NeedsLocalSource(RepairDiagnostic.None));
    }

    private static RepairSource Source(string path)
    {
        Assert.True(RepairSource.TryCreate(path, RepairSource.DefaultIndex, _ => FileAttributes.Archive, out RepairSource? source, out _));
        return source;
    }

    private sealed class Environment64 : IRepairEnvironment
    {
        public bool IsElevated => true;

        public string SystemDirectory => @"C:\Windows\System32";

        public int OemCodePage => 437;
    }
}
