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
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly UiServices _services;
    private readonly DispatcherQueueTimer _clock;
    private readonly ConsoleTranscript _transcript = new();
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

    public StatusBanner Banner { get; } = new();

    public Strings S => Strings.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedName), nameof(SelectedCommandLine), nameof(SelectedSummary), nameof(AcceptsSource))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial RepairRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCommandLine), nameof(SourceText), nameof(HasSource))]
    public partial RepairSource? Source { get; set; }

    [ObservableProperty]
    public partial string SourceProblem { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string Elapsed { get; set; } = string.Empty;

    public bool IsIdle => !IsRunning;

    public string PercentText => FormatPercent(Percent);

    public string SelectedName => Selected?.Name ?? string.Empty;

    public string SelectedCommandLine => Selected is not { } row ? string.Empty
        : AcceptsSource && Source is { } source ? RepairCommandCatalog.Resolve(row.Command.Id, SystemRepairEnvironment.Instance, source).CommandLine
        : row.CommandLine;

    public string SelectedSummary => Selected?.Summary ?? string.Empty;

    public bool AcceptsSource => Selected is { } row && RepairCommandCatalog.AcceptsSource(row.Command.Id);

    public bool HasSource => Source is not null;

    public string SourceText => Source is { } source ? S.RepairSourceLocal(source.Path, source.Index) : S.RepairSourceDefault;

    public static string FormatPercent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, RepairProgressParser.Complete))}%");

    public void Dispose()
    {
        _clock.Stop();
        TaskViewModel.Stop(_cancel);
    }

    public bool UseSource(string? path)
    {
        if (RepairSource.TryCreate(path, out RepairSource? source, out RepairSourceProblem problem))
        {
            Source = source;
            SourceProblem = string.Empty;
            return true;
        }

        SourceProblem = (path is null ? null : S.RepairSourceRefused(problem)) ?? string.Empty;
        return false;
    }

    [RelayCommand]
    private void ClearSource()
    {
        Source = null;
        SourceProblem = string.Empty;
    }

    partial void OnSelectedChanged(RepairRow? value)
    {
        if (!IsRunning)
        {
            Banner.Clear();
        }
    }

    private bool CanRun() => !IsRunning && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        RepairSource? source = AcceptsSource ? Source : null;
        Lines.Clear();
        _transcript.Clear();
        Mirror(_transcript.Echo($"> {SelectedCommandLine}"));
        Percent = 0;
        Banner.Clear();
        _started = DateTimeOffset.Now;
        Elapsed = Clock(TimeSpan.Zero);
        IsRunning = true;
        _clock.Start();
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        try
        {
            _runner ??= _services.Host.CreateRepairRunner();
            Show(await _runner.RunAsync(row.Command.Id, source, new Sink(this), cancel.Token));
        }
        finally
        {
            _clock.Stop();
            _cancel = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => TaskViewModel.Stop(_cancel);

    private void Apply(RepairUpdate update)
    {
        if (update.Percent is { } percent)
        {
            Percent = percent;
        }

        Mirror(_transcript.Apply(update));
    }

    private void Mirror(TranscriptChange change)
    {
        if (change.Action == TranscriptAction.None)
        {
            return;
        }

        if (change.Dropped >= 0 && change.Dropped < Lines.Count)
        {
            Lines.RemoveAt(change.Dropped);
        }

        TranscriptLine line = change.Line;
        var shown = new LogLine(line.Text, line.IsCommand ? LogLineKind.Command : line.Stream == RepairStream.Error ? LogLineKind.Error : LogLineKind.Output);
        if (change.Action == TranscriptAction.Replace && change.Index < Lines.Count)
        {
            Lines[change.Index] = shown;
            return;
        }

        Lines.Add(shown);
        LineAdded?.Invoke();
    }

    private void Show(RepairResult result)
    {
        Elapsed = Clock(result.Elapsed);
        Percent = result.FinalPercent;
        Banner.Show(result, S.RepairStatus(result, Elapsed));
    }

    private static string Clock(TimeSpan elapsed) => elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private sealed class Sink(RepairViewModel owner) : IProgress<RepairUpdate>
    {
        public void Report(RepairUpdate value) => owner._services.Post(() => owner.Apply(value));
    }
}
