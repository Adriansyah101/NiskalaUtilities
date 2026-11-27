namespace Niskala.UI.ViewModels;

public enum FlowStage
{
    Idle,
    Preparing,
    Removing,
    Cleanup,
    Finished,
}

public enum FlowExit
{
    CloseButton,
    Backdrop,
    Escape,
    Navigate,
    Palette,
}

public enum FlowExitResult
{
    Allow,
    CancelFlow,
    Refuse,
}

public sealed class FlowGate
{
    public FlowStage Stage { get; private set; }

    public bool IsLocked => Stage == FlowStage.Cleanup;

    public bool CanCancel => Stage is FlowStage.Preparing or FlowStage.Removing;

    public bool ShowsCloseButton => !IsLocked;

    public bool Enter(FlowStage stage)
    {
        bool changed = Stage != stage;
        Stage = stage;
        return changed;
    }

    public FlowExitResult Request(FlowExit exit) => (Stage, exit) switch
    {
        (FlowStage.Cleanup, _) => FlowExitResult.Refuse,
        (FlowStage.Preparing or FlowStage.Removing, FlowExit.Palette) => FlowExitResult.Allow,
        (FlowStage.Preparing or FlowStage.Removing, _) => FlowExitResult.CancelFlow,
        _ => FlowExitResult.Allow,
    };
}
