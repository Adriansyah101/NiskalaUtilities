using Niskala.Tracker.Tracing;

namespace Niskala.Tracker.Tests;

public sealed class KernelPathTranslatorTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static readonly KernelPathTranslator Translator = new(
        new Dictionary<string, string>
        {
            [@"\Device\HarddiskVolume1"] = "D:",
            [@"\Device\HarddiskVolume10"] = "C:",
        },
        Sid);

    [Theory]
    [InlineData(@"\Device\HarddiskVolume10\Program Files\Acme\acme.exe", @"C:\Program Files\Acme\acme.exe")]
    [InlineData(@"\Device\HarddiskVolume1\Data\a.txt", @"D:\Data\a.txt")]
    [InlineData(@"\??\C:\Windows\Temp\x.tmp", @"C:\Windows\Temp\x.tmp")]
    [InlineData(@"C:\Users\me\a.txt", @"C:\Users\me\a.txt")]
    [InlineData(@"\Device\Mup\server\share\setup.log", @"\\server\share\setup.log")]
    public void Files_map_to_drive_letters(string kernel, string expected) => Assert.Equal(expected, Translator.File(kernel));

    [Theory]
    [InlineData(@"\Device\HarddiskVolume2\x.txt")]
    [InlineData(@"\Device\HarddiskVolume100\x.txt")]
    [InlineData(@"\Device\NamedPipe\pipe")]
    public void Devices_without_a_drive_letter_are_unmapped(string kernel) => Assert.Null(Translator.File(kernel));

    [Theory]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\Acme", @"HKLM\SOFTWARE\Acme")]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\WOW6432Node\Acme", @"HKLM\SOFTWARE\WOW6432Node\Acme")]
    [InlineData(@"\REGISTRY\USER\" + Sid + @"\Software\Acme", @"HKCU\Software\Acme")]
    [InlineData(@"\REGISTRY\USER\" + Sid + @"_Classes\.acme", @"HKCU\Software\Classes\.acme")]
    [InlineData(@"\REGISTRY\USER\S-1-5-18\Software\Acme", @"HKU\S-1-5-18\Software\Acme")]
    [InlineData(@"\registry\machine\SYSTEM\CurrentControlSet\Services\Acme", @"HKLM\SYSTEM\CurrentControlSet\Services\Acme")]
    [InlineData(@"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Services\Acme", @"HKLM\SYSTEM\CurrentControlSet\Services\Acme")]
    [InlineData(@"\REGISTRY\MACHINE\SYSTEM\ControlSet001", @"HKLM\SYSTEM\CurrentControlSet")]
    [InlineData(@"\REGISTRY\MACHINE\SYSTEM\ControlSet002\Services\Acme", @"HKLM\SYSTEM\ControlSet002\Services\Acme")]
    public void Keys_map_to_hive_paths(string kernel, string expected) => Assert.Equal(expected, Translator.Key(kernel));

    [Theory]
    [InlineData(@"\REGISTRY\A\{guid}\Root")]
    [InlineData(@"\REGISTRY\MACHINEX\Acme")]
    [InlineData(@"\Device\HarddiskVolume1\Acme")]
    public void Other_registry_namespaces_are_unmapped(string kernel) => Assert.Null(Translator.Key(kernel));
}
