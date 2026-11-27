using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Niskala.Repair;
using Niskala.Repair.Output;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed class RepairRow(RepairCommand command, string commandLine)
{
    public RepairCommand Command { get; } = command;

    public string CommandLine { get; } = commandLine;

    public string Name => Strings.Current.RepairName(Command.Id);

    public string Summary => Strings.Current.RepairSummary(Command.Id);

    public string Traits
    {
        get
        {
            Strings s = Strings.Current;
            var traits = new List<string>(3) { Command.ChangesSystem ? s.RepairChangesSystem : s.RepairReadOnly };
            if (Command.NeedsRestart)
            {
                traits.Add(s.RepairNeedsRestart);
            }

            if (Command.IsLongRunning)
            {
                traits.Add(s.RepairLongRunning);
            }

            return string.Join(" · ", traits);
        }
    }

    public string Glyph => Command.Id switch
    {
        RepairCommandId.DismCheckHealth => "",
        RepairCommandId.DismRestoreHealth => "",
        RepairCommandId.SfcScanNow => "",
        RepairCommandId.DismComponentCleanup => "",
        RepairCommandId.NetshWinsockReset => "",
        RepairCommandId.NetshIpReset => "",
        RepairCommandId.FlushDns => "",
        _ => "",
    };
}

public enum LogLineKind
{
    Command,
    Output,
    Error,
}

public sealed class LogLine(string text, LogLineKind kind)
{
    public string Text { get; } = text;

    public LogLineKind Kind { get; } = kind;
}

public sealed partial class RepairViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 5000;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly UiServices _services;
    private readonly DispatcherQueueTimer _clock;
    private RepairProcessRunner? _runner;
    private CancellationTokenSource? _cancel;
    private DateTimeOffset _started;

    internal RepairViewModel(UiServices services)
    {
        _services = services;
        foreach (RepairCommand command in RepairCommandCatalog.All)
        {
            Commands.Add(new RepairRow(command, RepairCommandCatalog.Resolve(command.Id, SystemRepairEnvironment.Instance).CommandLine));
        }

        Selected = Commands[0];
        _clock = services.Dispatcher.CreateTimer();
        _clock.Interval = Tick;
        _clock.Tick += (_, _) => Elapsed = Clock(DateTimeOffset.Now - _started);
    }

    public event Action? LineAdded;

    public RepairRowCollection Commands { get; } = [];

    public LogLineCollection Lines { get; } = [];

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedName), nameof(SelectedCommandLine), nameof(SelectedSummary))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial RepairRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string LiveLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Elapsed { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Warning { get; set; }

    [ObservableProperty]
    public partial string? Result { get; set; }

    public bool IsIdle => !IsRunning;

    public string PercentText => FormatPercent(Percent);

    public string SelectedName => Selected?.Name ?? string.Empty;

    public string SelectedCommandLine => Selected?.CommandLine ?? string.Empty;

    public string SelectedSummary => Selected?.Summary ?? string.Empty;

    public static string FormatPercent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, RepairProgressParser.Complete))}%");

    public void Dispose()
    {
        _clock.Stop();
        _cancel?.Cancel();
    }

    private bool CanRun() => !IsRunning && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        Lines.Clear();
        Lines.Add(new LogLine($"> {row.CommandLine}", LogLineKind.Command));
        LineAdded?.Invoke();
        LiveLine = string.Empty;
        Percent = 0;
        Error = null;
        Warning = null;
        Result = null;
        _started = DateTimeOffset.Now;
        Elapsed = Clock(TimeSpan.Zero);
        IsRunning = true;
        _clock.Start();
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        try
        {
            _runner ??= _services.Host.CreateRepairRunner();
            Show(await _runner.RunAsync(row.Command.Id, new Sink(this), cancel.Token));
        }
        finally
        {
            _clock.Stop();
            _cancel = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cancel?.Cancel();

    private void Apply(RepairUpdate update)
    {
        if (update.Percent is { } percent)
        {
            Percent = percent;
        }

        if (!update.IsFinal)
        {
            LiveLine = update.Text;
            return;
        }

        LiveLine = string.Empty;
        if (update.Text.Length == 0 && Lines.Count > 0 && Lines[^1].Text.Length == 0)
        {
            return;
        }

        if (Lines.Count >= MaxLines)
        {
            Lines.RemoveAt(1);
        }

        Lines.Add(new LogLine(update.Text, update.Stream == RepairStream.Error ? LogLineKind.Error : LogLineKind.Output));
        LineAdded?.Invoke();
    }

    private void Show(RepairResult result)
    {
        Elapsed = Clock(result.Elapsed);
        LiveLine = string.Empty;
        Percent = result.FinalPercent;
        string text = S.RepairStatus(result, Elapsed);
        switch (result.Outcome)
        {
            case RepairOutcome.Succeeded:
                Result = text;
                break;
            case RepairOutcome.RestartRequired or RepairOutcome.IssuesFound or RepairOutcome.Cancelled:
                Warning = text;
                break;
            default:
                Error = text;
                break;
        }
    }

    private static string Clock(TimeSpan elapsed) => elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private sealed class Sink(RepairViewModel owner) : IProgress<RepairUpdate>
    {
        public void Report(RepairUpdate value) => owner._services.Post(() => owner.Apply(value));
    }
}
