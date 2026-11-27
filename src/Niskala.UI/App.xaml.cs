using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Niskala.UI.Services;
using Niskala.UI.Views;

namespace Niskala.UI;

public partial class App : Application
{
    private static readonly TimeSpan StrokeDrawn = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan SplashShown = TimeSpan.FromMilliseconds(1150);

    private MainWindow? _window;
    private UiServices? _services;

    public App()
    {
        InitializeComponent();
        Settings = UiSettings.Load();
        Strings.Use(Settings.Indonesian);
        UnhandledException += OnUnhandledException;
    }

    internal UiSettings Settings { get; private set; }

    public ElementTheme Theme => Settings.LightTheme ? ElementTheme.Light : ElementTheme.Dark;

    internal void Update(UiSettings settings)
    {
        Settings = settings;
        settings.Save(_services is { } services ? services.Host.LoggerFactory.CreateLogger<App>() : NullLogger.Instance);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var splash = new SplashWindow();
        Task shown = Task.WhenAll(splash.ShowAsync(), Task.Delay(SplashShown));
        await Task.Delay(StrokeDrawn);

        _services = new UiServices(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(this, _services);
        _window.Closed += (_, _) => _services.Dispose();
        await shown;
        _window.Activate();
        await splash.FadeOutAsync();
        splash.Close();
        _services.Updates.CheckOnStart();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_services is { } services)
        {
            UiLog.Unhandled(services.Host.LoggerFactory.CreateLogger<App>(), e.Exception);
        }
    }
}
