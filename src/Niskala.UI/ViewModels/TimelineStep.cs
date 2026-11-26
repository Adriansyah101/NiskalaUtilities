using CommunityToolkit.Mvvm.ComponentModel;

namespace Niskala.UI.ViewModels;

public enum StepState
{
    Pending,
    Active,
    Done,
    Failed,
    Skipped,
}

public sealed partial class TimelineStep(string title, bool isLast) : ObservableObject
{
    public string Title { get; } = title;

    public bool IsLast { get; } = isLast;

    public bool HasLine => !IsLast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsActive), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped))]
    public partial StepState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public bool IsPending => State == StepState.Pending;

    public bool IsActive => State == StepState.Active;

    public bool IsDone => State == StepState.Done;

    public bool IsFailed => State == StepState.Failed;

    public bool IsSkipped => State == StepState.Skipped;

    public void Start(string detail = "")
    {
        Detail = detail;
        State = StepState.Active;
    }

    public void Finish(string detail)
    {
        Detail = detail;
        State = StepState.Done;
    }

    public void Fail(string detail)
    {
        Detail = detail;
        State = StepState.Failed;
    }

    public void Skip(string detail)
    {
        Detail = detail;
        State = StepState.Skipped;
    }
}

public sealed partial class ProcessRow(int id, string? imagePath, bool adopted) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = imagePath is null ? $"PID {id}" : Path.GetFileName(imagePath);

    public string? ImagePath { get; } = imagePath;

    public bool Adopted { get; } = adopted;

    public string IdText => $"PID {Id}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    public partial bool Exited { get; set; }

    public bool IsRunning => !Exited;
}
