using Niskala.Repair;

namespace Niskala.Repair.Tests;

public sealed class RepairExitCodesTests
{
    [Theory]
    [InlineData(RepairCommandId.DismCheckHealth, 0, RepairOutcome.Succeeded, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.FlushDns, 0, RepairOutcome.Succeeded, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.NetshWinsockReset, 0, RepairOutcome.RestartRequired, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.NetshIpReset, 0, RepairOutcome.RestartRequired, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.SfcScanNow, 3010, RepairOutcome.RestartRequired, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.DismRestoreHealth, 3010, RepairOutcome.RestartRequired, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.DismCheckHealth, 740, RepairOutcome.Failed, RepairDiagnostic.ElevationRequired)]
    [InlineData(RepairCommandId.NetshIpReset, 5, RepairOutcome.Failed, RepairDiagnostic.AccessDenied)]
    [InlineData(RepairCommandId.DismComponentCleanup, 87, RepairOutcome.Failed, RepairDiagnostic.InvalidParameter)]
    [InlineData(RepairCommandId.DismRestoreHealth, unchecked((int)0x800F081F), RepairOutcome.Failed, RepairDiagnostic.SourceFilesMissing)]
    [InlineData(RepairCommandId.DismRestoreHealth, unchecked((int)0x800F0906), RepairOutcome.Failed, RepairDiagnostic.SourceDownloadFailed)]
    [InlineData(RepairCommandId.DismRestoreHealth, unchecked((int)0x800F0907), RepairOutcome.Failed, RepairDiagnostic.BlockedByPolicy)]
    [InlineData(RepairCommandId.DismComponentCleanup, unchecked((int)0x800F082F), RepairOutcome.Failed, RepairDiagnostic.PendingOperations)]
    [InlineData(RepairCommandId.ChkdskOnlineScan, 0, RepairOutcome.Succeeded, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.ChkdskOnlineScan, 1, RepairOutcome.Succeeded, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.ChkdskOnlineScan, 2, RepairOutcome.Succeeded, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.ChkdskOnlineScan, 3, RepairOutcome.IssuesFound, RepairDiagnostic.OfflineRepairNeeded)]
    [InlineData(RepairCommandId.DismCheckHealth, 3, RepairOutcome.Failed, RepairDiagnostic.None)]
    [InlineData(RepairCommandId.SfcScanNow, 1, RepairOutcome.Failed, RepairDiagnostic.None)]
    public void Exit_codes_map_to_an_outcome(RepairCommandId id, int exitCode, RepairOutcome outcome, RepairDiagnostic diagnostic)
    {
        Assert.Equal((outcome, diagnostic), RepairExitCodes.Interpret(RepairCommandCatalog.Get(id), exitCode));
    }

    [Theory]
    [InlineData(RepairOutcome.Succeeded, true)]
    [InlineData(RepairOutcome.RestartRequired, true)]
    [InlineData(RepairOutcome.IssuesFound, false)]
    [InlineData(RepairOutcome.Failed, false)]
    [InlineData(RepairOutcome.NotElevated, false)]
    [InlineData(RepairOutcome.StartFailed, false)]
    [InlineData(RepairOutcome.Cancelled, false)]
    public void Only_finished_repairs_count_as_success(RepairOutcome outcome, bool succeeded)
    {
        Assert.Equal(succeeded, new RepairResult(RepairCommandId.FlushDns, outcome, RepairDiagnostic.None, 0, null, null, TimeSpan.Zero).Succeeded);
    }
}
