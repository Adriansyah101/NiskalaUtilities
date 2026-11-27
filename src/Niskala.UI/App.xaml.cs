using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Niskala.UI.Services;

namespace Niskala.UI;

public partial class App : Application
{
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
        _services = new UiServices(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(this, _services);
        _window.Closed += (_, _) => _services.Dispose();
        await _window.ShowWhenRenderedAsync();
        SplashHandoff.Signal();
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
