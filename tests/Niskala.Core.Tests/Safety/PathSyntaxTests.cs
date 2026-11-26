using Niskala.Core.Safety;

namespace Niskala.Core.Tests.Safety;

public sealed class PathSyntaxTests
{
    [Theory]
    [InlineData(null, PathVerdict.Empty)]
    [InlineData("", PathVerdict.Empty)]
    [InlineData("   ", PathVerdict.Empty)]
    [InlineData("C:\\Temp\\a\u0001b", PathVerdict.InvalidCharacters)]
    [InlineData("C:\\Temp\\a\0b", PathVerdict.InvalidCharacters)]
    [InlineData("C:\\Temp\\*", PathVerdict.InvalidCharacters)]
    [InlineData("C:\\Temp\\a?b", PathVerdict.InvalidCharacters)]
    [InlineData("C:\\Temp\\a|b", PathVerdict.InvalidCharacters)]
    [InlineData(@"\\server\share\folder", PathVerdict.UncPath)]
    [InlineData(@"//server/share/folder", PathVerdict.UncPath)]
    [InlineData(@"\\?\UNC\server\share\folder", PathVerdict.UncPath)]
    [InlineData(@"\\.\C:\Temp", PathVerdict.DevicePath)]
    [InlineData(@"\\.\PhysicalDrive0", PathVerdict.DevicePath)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Temp", PathVerdict.DevicePath)]
    [InlineData(@"\\?\Volume{8d1f2a3b-0000-0000-0000-100000000000}\Temp", PathVerdict.DevicePath)]
    [InlineData(@"Temp\file.txt", PathVerdict.NotFullyQualified)]
    [InlineData(@"C:Temp", PathVerdict.NotFullyQualified)]
    [InlineData(@"\Temp", PathVerdict.NotFullyQualified)]
    [InlineData(@"C:", PathVerdict.NotFullyQualified)]
    [InlineData(@"C:\Temp\file.txt:hidden", PathVerdict.AlternateDataStream)]
    [InlineData(@"C:\Temp\file.txt::$DATA", PathVerdict.AlternateDataStream)]
    [InlineData(@"C:\Temp:stream\x", PathVerdict.AlternateDataStream)]
    [InlineData(@"C:\Temp\..\Windows", PathVerdict.TraversalSegment)]
    [InlineData(@"C:\Temp\.\x", PathVerdict.TraversalSegment)]
    [InlineData(@"C:\Windows.", PathVerdict.TrailingDotOrSpace)]
    [InlineData(@"C:\Windows \System32", PathVerdict.TrailingDotOrSpace)]
    [InlineData(@"C:\Temp\...", PathVerdict.TrailingDotOrSpace)]
    [InlineData(@"C:\Temp\CON", PathVerdict.ReservedDeviceName)]
    [InlineData(@"C:\Temp\nul.txt", PathVerdict.ReservedDeviceName)]
    [InlineData(@"C:\Temp\COM1", PathVerdict.ReservedDeviceName)]
    [InlineData(@"C:\Temp\lpt9.log", PathVerdict.ReservedDeviceName)]
    [InlineData("C:\\Temp\\COM\u00B9", PathVerdict.ReservedDeviceName)]
    [InlineData(@"C:\Temp\AUX .txt", PathVerdict.ReservedDeviceName)]
    public void Rejects_unsafe_syntax(string? input, PathVerdict expected)
    {
        Assert.Equal(expected, PathSyntax.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData(@"c:\temp", @"C:\temp")]
    [InlineData(@"C:/Program Files//Acme/", @"C:\Program Files\Acme")]
    [InlineData(@"\\?\C:\Temp\x", @"C:\Temp\x")]
    [InlineData(@"\??\C:\Temp\x", @"C:\Temp\x")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"d:\\", @"D:\")]
    [InlineData(@"C:\Temp\CONSOLE", @"C:\Temp\CONSOLE")]
    [InlineData(@"C:\Temp\file.with.dots.txt", @"C:\Temp\file.with.dots.txt")]
    [InlineData(@"C:\Temp\ leading space", @"C:\Temp\ leading space")]
    [InlineData(@"C:\PROGRA~1\Acme", @"C:\PROGRA~1\Acme")]
    public void Normalizes_valid_paths(string input, string expected)
    {
        Assert.Equal(PathVerdict.Allowed, PathSyntax.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Rejects_paths_longer_than_the_Win32_limit()
    {
        Assert.Equal(PathVerdict.TooLong, PathSyntax.TryNormalize(@"C:\" + new string('a', PathSyntax.MaxPathLength), out _));
    }

    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows", true, true)]
    [InlineData(@"C:\windows\system32", @"C:\Windows", true, false)]
    [InlineData(@"C:\WindowsTools", @"C:\Windows", false, false)]
    [InlineData(@"C:\Temp", @"C:\", true, false)]
    [InlineData(@"D:\Temp", @"C:\", false, false)]
    public void IsSameOrUnder_matches_whole_segments_only(string path, string root, bool expected, bool expectedSame)
    {
        Assert.Equal(expected, PathSyntax.IsSameOrUnder(path, root, out bool same));
        Assert.Equal(expectedSame, same);
    }
}
