using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Niskala.UI.Services;
using Niskala.Updater;

namespace Niskala.UI.ViewModels;

public sealed partial class UpdatesViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly Lazy<UpdateService> _updater;
    private readonly IReadOnlyList<NoteLine> _bundled = PatchNotes.Parse(PatchNotes.Bundled());
    private CancellationTokenSource? _cancel;
    private UpdateCheck? _check;

    internal UpdatesViewModel(UiServices services)
    {
        _services = services;
        _updater = new Lazy<UpdateService>(() => services.Host.CreateUpdateService(UpdateProduct.Desktop));
        ShowNotes();
    }

    public event Action? OfferReady;

    public event Action? ReviewRequested;

    public Strings S => Strings.Current;

    public UpdatePrompt Prompt { get; } = new();

    public bool ReviewPending { get; private set; }

    public string CurrentVersion => UpdateService.CurrentVersion.ToString();

    public string Copy => _updater.Value.Install.IsInstalled ? S.UpdateInstalledCopy(_updater.Value.Install.Root) : S.UpdatePortableCopy(_updater.Value.Install.Root);

    public string ApplyLabel => _updater.Value.Install.IsInstalled ? S.UpdateRestart : S.UpdateSavePackage;

    public bool AutoCheck
    {
        get => Application.Current is App app && !app.Settings.SkipUpdateCheck;
        set
        {
            if (Application.Current is App app && app.Settings.SkipUpdateCheck == value)
            {
                app.Update(app.Settings with { SkipUpdateCheck = !value });
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ApplyCommand))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ApplyCommand), nameof(CancelCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial string Latest { get; set; } = "—";

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Page { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Progress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Result { get; set; }

    public bool IsIdle => !IsChecking && !IsDownloading;

    public NoteRowCollection NoteRows { get; } = [];

    public void CheckOnStart()
    {
        if (AutoCheck)
        {
            _ = RunCheckAsync(quiet: true);
        }
    }

    public void Respond(UpdatePromptChoice choice)
    {
        Prompt.Close(choice);
        if (choice == UpdatePromptChoice.Later)
        {
            return;
        }

        _services.Navigate(Section.Updates);
        if (choice == UpdatePromptChoice.UpdateNow && ApplyCommand.CanExecute(null))
        {
            ApplyCommand.Execute(null);
            return;
        }

        ReviewPending = true;
        ReviewRequested?.Invoke();
    }

    public bool TakeReview()
    {
        bool pending = ReviewPending;
        ReviewPending = false;
        return pending;
    }

    public void Relabel()
    {
        OnPropertyChanged(nameof(Copy));
        OnPropertyChanged(nameof(ApplyLabel));
        Status = _check is null ? string.Empty : S.UpdateStatusText(_check.Status, _check.Latest?.ToString());
        ShowNotes();
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        if (_updater.IsValueCreated)
        {
            _updater.Value.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task CheckAsync() => RunCheckAsync(quiet: false);

    private bool CanApply() => IsIdle && IsAvailable;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_check is not { } check)
        {
            return;
        }

        Error = null;
        Result = null;
        Percent = 0;
        Progress = string.Empty;
        IsDownloading = true;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        try
        {
            VerifiedDownload download = await _updater.Value.DownloadAsync(check, new Sink(this), cancel.Token);
            Progress = S.UpdateVerified(download.Asset.Name);
            UpdateLaunch launch = _updater.Value.Apply(download, restart: true);
            if (launch.Action == UpdateAction.InstallerStarted)
            {
                Application.Current.Exit();
                return;
            }

            Result = S.UpdateSaved(launch.Path, _updater.Value.Install.Root);
        }
        catch (OperationCanceledException)
        {
            Progress = string.Empty;
            Result = S.UpdateDownloadCancelled;
        }
        catch (Exception ex) when (Describe(ex) is { } text)
        {
            Progress = string.Empty;
            Error = text;
        }
        finally
        {
            _cancel = null;
            IsDownloading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private void OpenPage()
    {
        if (Page is { } page)
        {
            ShellLauncher.OpenLink(page);
        }
    }

    private async Task RunCheckAsync(bool quiet)
    {
        Error = null;
        Result = null;
        Status = S.UpdateChecking;
        IsChecking = true;
        try
        {
            UpdateCheck check = await _updater.Value.CheckAsync(CancellationToken.None);
            _check = check;
            IsAvailable = check.IsAvailable && _updater.Value.PackageFor(check) is not null && check.Assets.Checksums is not null;
            Latest = check.Latest?.ToString() ?? "—";
            Page = check.Page?.AbsoluteUri;
            ShowNotes();
            Status = check.Published is { } published
                ? $"{S.UpdateStatusText(check.Status, check.Latest?.ToString())} {S.UpdatePublished(Format.When(published))}"
                : S.UpdateStatusText(check.Status, check.Latest?.ToString());
            if (check.IsAvailable && !IsAvailable)
            {
                Error = S.UpdateIncomplete;
            }

            if (Prompt.Offer(check, automatic: quiet, IsAvailable))
            {
                OfferReady?.Invoke();
            }
        }
        catch (Exception ex) when (Describe(ex) is { } text)
        {
            Status = quiet ? S.UpdateNotChecked : string.Empty;
            Error = quiet ? null : text;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ShowNotes()
    {
        NoteRows.Clear();
        if (_check is { Latest: { } latest, Notes: { Length: > 0 } notes } && !Covers(_bundled, latest))
        {
            NoteRows.Add(new NoteRow(new NoteLine(NoteKind.Title, S.UpdateNotesOnGitHub(latest.ToString()))));
            foreach (NoteLine line in PatchNotes.Parse(notes))
            {
                NoteRows.Add(new NoteRow(line.Kind == NoteKind.Title ? line with { Kind = NoteKind.Heading } : line));
            }
        }

        foreach (NoteLine line in _bundled)
        {
            NoteRows.Add(new NoteRow(line));
        }
    }

    public static bool Covers(IReadOnlyList<NoteLine> notes, ReleaseVersion version) =>
        notes.Any(l => l.Kind == NoteKind.Heading && l.Text.StartsWith($"v{version} ", StringComparison.OrdinalIgnoreCase));

    private string? Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => S.UpdateRateLimited,
        HttpRequestException http => S.UpdateUnreachable(http.Message),
        TimeoutException => S.UpdateTimedOut,
        UpdateIntegrityException integrity => S.UpdateRejected(integrity.Message),
        JsonException or FormatException => S.UpdateBadAnswer,
        IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception => ex.Message,
        _ => null,
    };

    private void Report(DownloadProgress progress)
    {
        Percent = progress.Percent ?? 0;
        Progress = S.UpdateDownloading(progress.Name, Format.Bytes(progress.Received), progress.Total is { } total ? Format.Bytes(total) : null);
    }

    private sealed class Sink(UpdatesViewModel owner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => owner._services.Post(() => owner.Report(value));
    }
}
