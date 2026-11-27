using System.Text.RegularExpressions;
using Niskala.UI.Services;
using Windows.System;

namespace Niskala.UI.Tests;

public sealed partial class ShellContractTests
{
    private static readonly string Repository = FindRepository();

    [Fact]
    public void The_palette_shortcut_is_ctrl_k()
    {
        Assert.Equal(VirtualKey.K, Shortcuts.PaletteKey);
        Assert.Equal(VirtualKeyModifiers.Control, Shortcuts.PaletteModifiers);
        Assert.Contains($"Text=\"{Shortcuts.PaletteBadge}\"", Read(@"src\Niskala.UI\MainWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_keyboard_accelerator_hides_its_automatic_tooltip()
    {
        int owners = 0;
        foreach (string file in UiSources("*.cs"))
        {
            string code = File.ReadAllText(file);
            foreach (Match match in AcceleratorOwner().Matches(code))
            {
                owners++;
                string owner = match.Groups["owner"].Value;
                Assert.True(
                    code.Contains($"{owner}.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: {owner} shows its shortcut as a tooltip");
            }

            Assert.DoesNotMatch(VisiblePlacement(), code);
        }

        foreach (string file in UiSources("*.xaml"))
        {
            string markup = File.ReadAllText(file);
            Assert.DoesNotContain("<KeyboardAccelerator ", markup, StringComparison.Ordinal);
            Assert.DoesNotMatch(VisiblePlacementMarkup(), markup);
        }

        Assert.True(owners > 0);
    }

    [Fact]
    public void The_launcher_and_the_app_agree_on_the_splash_signal()
    {
        string header = Read(@"native\Niskala.Launcher\src\Splash.h");

        Assert.Contains($"L\"{SplashHandoff.ReadyEvent.Replace(@"\", @"\\", StringComparison.Ordinal)}\"", header, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\", SplashHandoff.ReadyEvent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    [InlineData(3, true, true)]
    [InlineData(9, true, true)]
    public void The_splash_waits_for_rendered_frames_of_the_loaded_window(int frames, bool loaded, bool ready)
    {
        Assert.Equal(ready, SplashHandoff.IsReady(frames, loaded));
    }

    [Fact]
    public void The_app_signals_a_waiting_splash_and_ignores_a_missing_one()
    {
        if (EventWaitHandle.TryOpenExisting(SplashHandoff.ReadyEvent, out EventWaitHandle? busy))
        {
            busy.Dispose();
            Assert.Skip("A launcher splash is open on this desktop.");
        }

        Assert.False(SplashHandoff.Signal());
        using var waiting = new EventWaitHandle(false, EventResetMode.AutoReset, SplashHandoff.ReadyEvent);

        Assert.True(SplashHandoff.Signal());
        Assert.True(waiting.WaitOne(0));
        Assert.False(waiting.WaitOne(0));
    }

    [Fact]
    public void The_splash_fades_out_in_one_short_step_after_a_quick_fade_in()
    {
        string header = Read(@"native\Niskala.Launcher\src\Splash.h");

        double fadeIn = Timing(header, "FadeIn");
        double fadeOut = Timing(header, "FadeOut");

        Assert.InRange(fadeIn, 120, 150);
        Assert.InRange(fadeOut, 180, 220);
        Assert.DoesNotContain("SetTimer", Read(@"native\Niskala.Launcher\src\Splash.cpp"), StringComparison.Ordinal);
        Assert.Contains("QueryPerformanceCounter", Read(@"native\Niskala.Launcher\src\Splash.cpp"), StringComparison.Ordinal);
        Assert.Contains("DwmFlush", Read(@"native\Niskala.Launcher\src\Splash.cpp"), StringComparison.Ordinal);
        Assert.Contains("AC_SRC_ALPHA", Read(@"native\Niskala.Launcher\src\Splash.cpp"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_text_shown_through_a_visibility_function_can_turn_back_into_null()
    {
        string models = string.Concat(Directory.EnumerateFiles(Path.Combine(Repository, "src", "Niskala.UI", "ViewModels"), "*.cs").Select(File.ReadAllText));
        var checkedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in UiSources("*.xaml"))
        {
            foreach (Match match in TextFunction().Matches(File.ReadAllText(file)))
            {
                string name = match.Groups["name"].Value;
                checkedNames.Add(name);
                Assert.False(
                    Regex.IsMatch(models, $@"\bstring\? {name}\b", RegexOptions.CultureInvariant),
                    $"{Path.GetFileName(file)} hides or shows by {name}, which can become null; x:Bind skips the function for null and leaves the old state on screen");
            }
        }

        Assert.Contains("Error", checkedNames);
        Assert.Contains("Message", checkedNames);
    }

    private static double Timing(string header, string name)
    {
        Match match = Regex.Match(header, $@"double {name} = (?<value>[0-9.]+);", RegexOptions.CultureInvariant);
        Assert.True(match.Success, name);
        return double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> UiSources(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Repository, "src", "Niskala.UI"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Repository, relative));

    private static string FindRepository()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Niskala.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }

    [GeneratedRegex(@"(?<owner>\w+)\.KeyboardAccelerators\.Add\(", RegexOptions.CultureInvariant)]
    private static partial Regex AcceleratorOwner();

    [GeneratedRegex(@"KeyboardAcceleratorPlacementMode\.(Auto|Visible)", RegexOptions.CultureInvariant)]
    private static partial Regex VisiblePlacement();

    [GeneratedRegex(@"KeyboardAcceleratorPlacementMode=""(Auto|Visible)""", RegexOptions.CultureInvariant)]
    private static partial Regex VisiblePlacementMarkup();

    [GeneratedRegex(@"Format\.(HasText|VisibleText|NoText)\((\w+\.)*(?<name>\w+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TextFunction();
}
