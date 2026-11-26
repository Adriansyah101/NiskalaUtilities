namespace Niskala.Repair;

public enum RepairOutcome
{
    Succeeded,
    RestartRequired,
    IssuesFound,
    Failed,
    NotElevated,
    StartFailed,
    Cancelled,
}

public enum RepairDiagnostic
{
    None,
    ElevationRequired,
    AccessDenied,
    InvalidParameter,
    SourceFilesMissing,
    SourceDownloadFailed,
    BlockedByPolicy,
    PendingOperations,
    OfflineRepairNeeded,
    ProgramMissing,
}

public static class RepairExitCodes
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int AccessDenied = 5;
    public const int InvalidParameter = 87;
    public const int ElevationRequired = 740;
    public const int RestartRequired = 3010;
    public const int SourceFilesMissing = unchecked((int)0x800F081F);
    public const int SourceDownloadFailed = unchecked((int)0x800F0906);
    public const int BlockedByPolicy = unchecked((int)0x800F0907);
    public const int PendingOperations = unchecked((int)0x800F082F);

    private const int ChkdskFixed = 1;
    private const int ChkdskCleanupSkipped = 2;
    private const int ChkdskNeedsOfflineRepair = 3;

    public static (RepairOutcome Outcome, RepairDiagnostic Diagnostic) Interpret(RepairCommand command, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == RepairCommandId.ChkdskOnlineScan)
        {
            switch (exitCode)
            {
                case ChkdskFixed or ChkdskCleanupSkipped:
                    return (RepairOutcome.Succeeded, RepairDiagnostic.None);
                case ChkdskNeedsOfflineRepair:
                    return (RepairOutcome.IssuesFound, RepairDiagnostic.OfflineRepairNeeded);
            }
        }

        return exitCode switch
        {
            Success => (command.NeedsRestart ? RepairOutcome.RestartRequired : RepairOutcome.Succeeded, RepairDiagnostic.None),
            RestartRequired => (RepairOutcome.RestartRequired, RepairDiagnostic.None),
            ElevationRequired => (RepairOutcome.Failed, RepairDiagnostic.ElevationRequired),
            AccessDenied => (RepairOutcome.Failed, RepairDiagnostic.AccessDenied),
            InvalidParameter => (RepairOutcome.Failed, RepairDiagnostic.InvalidParameter),
            SourceFilesMissing => (RepairOutcome.Failed, RepairDiagnostic.SourceFilesMissing),
            SourceDownloadFailed => (RepairOutcome.Failed, RepairDiagnostic.SourceDownloadFailed),
            BlockedByPolicy => (RepairOutcome.Failed, RepairDiagnostic.BlockedByPolicy),
            PendingOperations => (RepairOutcome.Failed, RepairDiagnostic.PendingOperations),
            _ => (RepairOutcome.Failed, RepairDiagnostic.None),
        };
    }
}
