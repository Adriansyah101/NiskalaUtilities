using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Niskala.Repair;

namespace Niskala.UI.ViewModels;

public sealed partial class StatusBanner : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity Severity { get; private set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool OffersSource { get; private set; }

    public bool IsOpen => !string.IsNullOrWhiteSpace(Message);

    public static InfoBarSeverity SeverityOf(RepairOutcome outcome) => outcome switch
    {
        RepairOutcome.Succeeded => InfoBarSeverity.Success,
        RepairOutcome.RestartRequired or RepairOutcome.IssuesFound or RepairOutcome.Cancelled => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error,
    };

    public void Clear()
    {
        Message = string.Empty;
        OffersSource = false;
        Severity = InfoBarSeverity.Informational;
    }

    public void Show(InfoBarSeverity severity, string? message, bool offersSource = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Clear();
            return;
        }

        Message = string.Empty;
        Severity = severity;
        OffersSource = offersSource;
        Message = message.Trim();
    }

    public void Show(RepairResult result, string message)
    {
        ArgumentNullException.ThrowIfNull(result);
        Show(SeverityOf(result.Outcome), message, RepairCommandCatalog.AcceptsSource(result.Command) && RepairExitCodes.NeedsLocalSource(result.Diagnostic));
    }
}
