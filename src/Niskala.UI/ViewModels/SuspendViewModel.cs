using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Interop.Processes;
using Niskala.Optimizer.Processes;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed partial class SuspendRow : ObservableObject
{
    private readonly SuspendViewModel _owner;

    internal SuspendRow(SuspendViewModel owner, ProcessCandidate candidate, bool capped)
    {
        _owner = owner;
        Candidate = candidate;
        IsCapped = capped;
    }

    public ProcessCandidate Candidate { get; private set; }

    public string Name => Candidate.Name;

    public string Detail => string.Join(" · ", new[]
    {
        string.Create(Format.Culture, $"PID {Candidate.Id}"),
        string.Create(Format.Culture, $"CPU {Candidate.CpuPercent:0.0}%"),
        Candidate.HasWindow ? Strings.Current.HasWindow : Strings.Current.Background,
        Candidate.ImagePath,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public bool CanRestrain => Candidate.CanRestrain;

    public string Refusal => Candidate.CanRestrain ? string.Empty : Strings.Current.ProcessRefusal(Candidate.Refusal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsHeld), nameof(IsFree))]
    public partial bool IsCapped { get; set; }

    public string State => Candidate.Restraint switch
    {
        RestraintKind.Suspended => Strings.Current.Suspended,
        RestraintKind.Efficiency => Strings.Current.EfficiencyMode,
        _ when IsCapped => Strings.Current.CpuCapped,
        _ => Candidate.IsIdleBackground ? Strings.Current.IdleBackground : string.Empty,
    };

    public bool IsHeld => Candidate.Restraint is not null || IsCapped;

    public bool IsFree => CanRestrain && !IsHeld;

    [RelayCommand]
    private void Suspend() => _owner.Apply(this, RestraintKind.Suspended);

    [RelayCommand]
    private void Efficiency() => _owner.Apply(this, RestraintKind.Efficiency);

    [RelayCommand]
    private void Cap() => _owner.Cap(this);

    [RelayCommand]
    private void Release() => _owner.Release(this);

    internal void Update(RestraintKind? restraint)
    {
        Candidate = Candidate with { Restraint = restraint };
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsHeld));
        OnPropertyChanged(nameof(IsFree));
    }
}

public sealed partial class SuspendViewModel : TaskViewModel
{
    public const int CapPercent = 20;

    private static readonly TimeSpan Sample = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<int, CpuCapJob> _caps = [];
    private IReadOnlyList<ProcessCandidate> _all = [];
    private ProcessRestrainer? _restrainer;

    internal SuspendViewModel(UiServices services)
        : base(services)
    {
    }

    public SuspendRowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial bool IdleOnly { get; set; } = true;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool IsLoaded => _all.Count > 0;

    partial void OnIdleOnlyChanged(bool value) => Show();

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async token =>
    {
        Status = S.MeasuringCpu;
        _restrainer ??= Services.Host.CreateProcessRestrainer();
        ProcessRestrainer restrainer = _restrainer;
        _all = await Task.Run(() => restrainer.ListAsync(Sample, token), token);
        Show();
    });

    [RelayCommand]
    private void ReleaseAll()
    {
        foreach (SuspendRow row in Rows.Where(r => r.IsHeld).ToList())
        {
            Release(row);
        }

        if (_restrainer is { } restrainer)
        {
            restrainer.UndoAll();
        }
    }

    internal void Apply(SuspendRow row, RestraintKind kind)
    {
        if (_restrainer is not { } restrainer)
        {
            return;
        }

        RestraintResult result = kind == RestraintKind.Suspended ? restrainer.Suspend(row.Candidate.Id) : restrainer.SetEfficiency(row.Candidate.Id);
        Report(row, result, kind);
    }

    internal void Cap(SuspendRow row)
    {
        if (_restrainer is not { } restrainer)
        {
            return;
        }

        RestraintResult result = restrainer.Cap(row.Candidate.Id, CapPercent, out CpuCapJob? job);
        if (job is not null)
        {
            _caps[row.Candidate.Id] = job;
            row.IsCapped = true;
        }

        Error = (result.Outcome == RestraintOutcome.Applied ? null : S.RestraintFailed(row.Name, result)) ?? string.Empty;
    }

    internal void Release(SuspendRow row)
    {
        if (_caps.Remove(row.Candidate.Id, out CpuCapJob? job))
        {
            job.Dispose();
            row.IsCapped = false;
        }

        if (row.Candidate.Restraint is not null && _restrainer is { } restrainer)
        {
            RestraintResult result = restrainer.Undo(row.Candidate.Id);
            Report(row, result, null);
        }
    }

    public override void Dispose()
    {
        foreach (CpuCapJob job in _caps.Values)
        {
            job.Dispose();
        }

        _caps.Clear();
        base.Dispose();
    }

    private void Report(SuspendRow row, RestraintResult result, RestraintKind? kind)
    {
        if (result.Outcome is RestraintOutcome.Applied or RestraintOutcome.Undone or RestraintOutcome.NotRestrained)
        {
            Error = string.Empty;
            row.Update(kind);
            return;
        }

        Error = S.RestraintFailed(row.Name, result);
    }

    private void Show()
    {
        Rows.Clear();
        foreach (ProcessCandidate candidate in _all.Where(c => !IdleOnly || (c.CanRestrain && c.IsIdleBackground) || c.Restraint is not null || _caps.ContainsKey(c.Id)))
        {
            Rows.Add(new SuspendRow(this, candidate, _caps.ContainsKey(candidate.Id)));
        }

        Summary = S.ProcessSummary(_all.Count, _all.Count(c => c.CanRestrain && c.IsIdleBackground), _all.Count(c => c.Restraint is not null) + _caps.Count);
        OnPropertyChanged(nameof(IsLoaded));
    }
}
