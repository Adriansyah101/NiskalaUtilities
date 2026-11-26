using Niskala.Core.Safety.Registry;

namespace Niskala.Core.Tests.Safety;

public sealed class ServiceImagePathTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\Acme\svc.exe"" --run", @"C:\Program Files\Acme\svc.exe")]
    [InlineData(@"C:\Program Files\Acme\acme svc.exe -k", @"C:\Program Files\Acme\acme svc.exe")]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs", @"C:\Windows\System32\svchost.exe")]
    [InlineData(@"%windir%\system32\spoolsv.exe", @"C:\Windows\system32\spoolsv.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\acme.sys", @"C:\Windows\System32\drivers\acme.sys")]
    [InlineData(@"System32\drivers\tcpip.sys", @"C:\Windows\System32\drivers\tcpip.sys")]
    [InlineData(@"\??\C:\Program Files\Acme\acme.sys", @"C:\Program Files\Acme\acme.sys")]
    public void Resolves_image_paths(string imagePath, string expected)
    {
        Assert.Equal(expected, ServiceImagePath.Resolve(imagePath, @"C:\Windows"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relative\acme.exe")]
    [InlineData(@"\\server\share\acme.exe")]
    public void Unknown_image_paths_resolve_to_null(string? imagePath)
    {
        Assert.Null(ServiceImagePath.Resolve(imagePath, @"C:\Windows"));
    }
}
