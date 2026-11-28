using System.Collections.ObjectModel;

namespace Niskala.UI.ViewModels;

public sealed partial class AppItemCollection : ObservableCollection<AppItem>;

public sealed partial class LeftoverTreeRowCollection : ObservableCollection<LeftoverTreeRow>;

public sealed partial class ActivityRowCollection : ObservableCollection<ActivityRow>;

public sealed partial class TimelineStepCollection : ObservableCollection<TimelineStep>;

public sealed partial class ProcessRowCollection : ObservableCollection<ProcessRow>;

public sealed partial class LogItemCollection : ObservableCollection<LogItem>;

public sealed partial class CleanRuleRowCollection : ObservableCollection<CleanRuleRow>;

public sealed partial class HindranceRowCollection : ObservableCollection<HindranceRow>;

public sealed partial class IssueRowCollection : ObservableCollection<IssueRow>;

public sealed partial class ResourceRowCollection : ObservableCollection<ResourceRow>;

public sealed partial class SuspendRowCollection : ObservableCollection<SuspendRow>;

public sealed partial class DriveRowCollection : ObservableCollection<DriveRow>;

public sealed partial class FragmentRowCollection : ObservableCollection<FragmentRow>;

public sealed partial class SummaryRowCollection : ObservableCollection<SummaryRow>;

public sealed partial class RepairRowCollection : ObservableCollection<RepairRow>;

public sealed partial class LogLineCollection : ObservableCollection<LogLine>;

public sealed partial class TransactionItemCollection : ObservableCollection<TransactionItem>;

public sealed partial class PaletteEntryCollection : ObservableCollection<PaletteEntry>;

public sealed partial class NoteRowCollection : ObservableCollection<NoteRow>;

public sealed partial class NavCategoryCollection : ObservableCollection<NavCategory>;

public sealed partial class NavPageCollection : ObservableCollection<NavPage>;

public sealed partial class TextList : List<string>;

public sealed partial class ReasonList : List<ReasonRow>;

public sealed partial class ChangeList : List<ChangeRow>;

public sealed partial class RecordList : List<RecordRow>;
