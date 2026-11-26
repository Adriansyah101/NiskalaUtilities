using System.Reflection;
using System.Security.Principal;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Niskala.Interop.Desktop;
using Niskala.UI.Services;
using Niskala.UI.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Niskala.UI;

public sealed partial class MainWindow : Window
{
    private const int StartWidth = 1320;
    private const int StartHeight = 860;
    private const int MinimumWidth = 960;
    private const int MinimumHeight = 640;

    private readonly App _app;
    private readonly UiServices _services;
    private readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();

    public MainWindow(App app, UiServices services)
    {
        _app = app;
        _services = services;
        InitializeComponent();

        nint handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        Pickers.Owner = handle;
        double scale = WindowNative.Scale(handle);
        _presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
        _presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        AppWindow.SetPresenter(_presenter);
        AppWindow.Resize(new SizeInt32((int)(StartWidth * scale), (int)(StartHeight * scale)));
        nint icon = WindowChrome.ApplicationIcon();
        if (icon != 0)
        {
            AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(icon));
        }

        ExtendsContentIntoTitleBar = true;

        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(TitleBar);

        var palette = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        palette.Invoked += (_, e) =>
        {
            e.Handled = true;
            _services.Palette.Open();
        };
        Root.KeyboardAccelerators.Add(palette);

        Launcher.SizeChanged += (_, _) => UpdatePassthrough();
        Root.SizeChanged += (_, _) => UpdatePassthrough();
        services.ThemeToggleRequested += ToggleTheme;
        services.LanguageToggleRequested += ToggleLanguage;
        services.MainWindowHidden += hidden =>
        {
            if (hidden)
            {
                _presenter.Minimize();
            }
            else
            {
                _presenter.Restore();
                Activate();
            }
        };

        ApplyTheme();
        Rebuild();
    }

    private static string Version { get; } =
        typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? string.Empty;

    private static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private void Rebuild()
    {
        Strings s = Strings.Current;
        AppName.Text = s.AppTitle;
        VersionText.Text = Version;
        AdminText.Text = s.Administrator;
        AdminPill.Visibility = IsElevated ? Visibility.Visible : Visibility.Collapsed;
        LauncherText.Text = s.PaletteLauncher;
        Host.Child = new ShellView(_services);
    }

    private void OnLauncherClick(object sender, RoutedEventArgs e) => _services.Palette.Open();

    private void ToggleTheme()
    {
        _app.Update(_app.Settings with { LightTheme = !_app.Settings.LightTheme });
        ApplyTheme();
    }

    private void ToggleLanguage()
    {
        _app.Update(_app.Settings with { Language = _app.Settings.Indonesian ? "en" : "id" });
        Strings.Use(_app.Settings.Indonesian);
        _services.Relabel();
        Rebuild();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _app.Theme;
        bool light = _app.Theme == ElementTheme.Light;
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? Color.FromArgb(255, 0x1F, 0x22, 0x2B) : Color.FromArgb(255, 0xE6, 0xE7, 0xEB);
        bar.ButtonInactiveForegroundColor = light ? Color.FromArgb(255, 0x8A, 0x8E, 0x9C) : Color.FromArgb(255, 0x6C, 0x6F, 0x7C);
        bar.ButtonHoverBackgroundColor = light ? Color.FromArgb(255, 0xE9, 0xEB, 0xF0) : Color.FromArgb(255, 0x1E, 0x20, 0x27);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = light ? Color.FromArgb(255, 0xDD, 0xE0, 0xE8) : Color.FromArgb(255, 0x25, 0x27, 0x2F);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    private void UpdatePassthrough()
    {
        if (Launcher.XamlRoot is not { } root || Launcher.ActualWidth == 0)
        {
            return;
        }

        double scale = root.RasterizationScale;
        Windows.Foundation.Rect bounds = Launcher.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, Launcher.ActualWidth, Launcher.ActualHeight));
        var rect = new RectInt32((int)(bounds.X * scale), (int)(bounds.Y * scale), (int)(bounds.Width * scale), (int)(bounds.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
        CaptionColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }
}
