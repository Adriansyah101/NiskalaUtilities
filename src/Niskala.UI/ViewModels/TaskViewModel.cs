using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public abstract partial class TaskViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancel;

    private protected TaskViewModel(UiServices services) => Services = services;

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Info { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Status { get; set; }

    private protected UiServices Services { get; }

    [RelayCommand]
    public void Cancel() => Stop(_cancel);

    public virtual void Dispose()
    {
        Stop(_cancel);
        GC.SuppressFinalize(this);
    }

    public static bool Stop(CancellationTokenSource? cancel)
    {
        if (cancel is null || cancel.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            cancel.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private protected async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsBusy = true;
        Error = string.Empty;
        Info = string.Empty;
        try
        {
            await work(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Info = S.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            UiLog.TaskFailed(Services.Host.LoggerFactory.CreateLogger(GetType().Name), ex);
            Error = ex.Message;
        }
        finally
        {
            _cancel = null;
            IsBusy = false;
            Status = null;
        }
    }

    private protected void Post(Action action) => Services.Post(action);
}
