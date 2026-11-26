using System.Text;
using Niskala.Scanner.Detection;

namespace Niskala.Scanner.Tests;

public sealed class NsisProbeTests
{
    private static byte[] Image(int headerOffset, int flags, int length = 8192)
    {
        byte[] data = new byte[length];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(flags).CopyTo(data, headerOffset);
        BitConverter.GetBytes(0xDEADBEEF).CopyTo(data, headerOffset + 4);
        Encoding.ASCII.GetBytes("NullsoftInst").CopyTo(data, headerOffset + 8);
        return data;
    }

    [Fact]
    public void Finds_an_aligned_uninstaller_header()
    {
        NsisProbeResult result = FileExecutableProbe.Scan(Image(4096, flags: 1));

        Assert.True(result.IsNsis);
        Assert.True(result.IsUninstaller);
        Assert.Equal(4096, result.HeaderOffset);
    }

    [Fact]
    public void Installer_header_without_the_uninstall_flag()
    {
        NsisProbeResult result = FileExecutableProbe.Scan(Image(1024, flags: 0));

        Assert.True(result.IsNsis);
        Assert.False(result.IsUninstaller);
    }

    [Fact]
    public void Ignores_a_header_off_the_512_byte_grid()
    {
        Assert.False(FileExecutableProbe.Scan(Image(1000, flags: 1)).IsNsis);
    }

    [Fact]
    public void Ignores_files_that_are_not_PE()
    {
        byte[] data = Image(1024, flags: 1);
        data[0] = (byte)'P';
        Assert.False(FileExecutableProbe.Scan(data).IsNsis);
    }

    [Fact]
    public void Missing_file_is_not_NSIS()
    {
        Assert.False(FileExecutableProbe.Instance.ProbeNsis(@"C:\definitely\missing\uninst.exe").IsNsis);
    }
}
