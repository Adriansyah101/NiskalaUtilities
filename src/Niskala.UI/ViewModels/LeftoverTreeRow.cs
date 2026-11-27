using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Scanner.Leftovers;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed record ReasonRow(string Title, string Weight, string Detail);

public sealed partial class LeftoverTreeRow : ObservableObject
{
    private const double IndentStep = 22;

    private readonly Func<LeftoverTreeRow, Task> _toggle;
    private List<LeftoverTreeRow>? _children;

    internal LeftoverTreeRow(LeftoverItem leftover, LeftoverNode node, LeftoverTreeRow? parent, bool isChecked, Func<LeftoverTreeRow, Task> toggle)
    {
        Leftover = leftover;
        Node = node;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        IsChecked = isChecked;
        _toggle = toggle;
        Strings s = Strings.Current;
        Reasons = parent?.Reasons ?? [.. leftover.Reasons.Select(r => new ReasonRow(s.Signal(r.Signal), r.Weight >= 0 ? $"+{r.Weight}" : $"{r.Weight}", r.Detail))];
    }

    public LeftoverItem Leftover { get; }

    public LeftoverNode Node { get; }

    public LeftoverTreeRow? Parent { get; }

    public int Depth { get; }

    public bool IsRoot => Parent is null;

    public IReadOnlyList<LeftoverTreeRow>? Children => _children;

    public double Indent => Depth * IndentStep;

    public bool CanExpand => Node.HasChildren && !Node.IsLink;

    public string Chevron => IsExpanded ? "" : "";

    public string Glyph => Node.Kind switch
    {
        LeftoverKind.Folder => Node.IsLink ? "" : "",
        LeftoverKind.File => "",
        LeftoverKind.RegistryKey => "",
        LeftoverKind.RegistryValue => "",
        LeftoverKind.ScheduledTask => "",
        _ => "",
    };

    public string Title => IsRoot ? FullPath : ValueLabel ?? Node.Name;

    public string FullPath => Node.Kind == LeftoverKind.RegistryValue ? $"{Node.Path} → {ValueLabel}" : Node.Path;

    public string KindText => Node.Kind == LeftoverKind.RegistryValue && Node.ValueType is { } type ? type : Strings.Current.Kind(Node.Kind);

    public string Detail => Node.Kind switch
    {
        LeftoverKind.RegistryValue => $"{Node.ValueType} · {Node.Data}",
        LeftoverKind.File or LeftoverKind.Folder => Join(SizeText, Modified),
        _ => Modified,
    };

    public string LocationLabel => IsFile ? Strings.Current.FilePath : Strings.Current.RegistryPath;

    public string Location => Node.Path;

    public bool HasValueName => Node.Kind == LeftoverKind.RegistryValue;

    public string ValueNameText => ValueLabel ?? string.Empty;

    public string? Data => Node.Data;

    public bool HasData => Node.Data is not null;

    public string SizeText => (IsRoot ? Leftover.Files : Node.Files, IsRoot ? Leftover.Bytes : Node.Bytes, Node.Kind) switch
    {
        (_, { } bytes, LeftoverKind.File) => Format.Bytes(bytes),
        ({ } files, { } bytes, LeftoverKind.Folder) => Strings.Current.FilesAndBytes(files, Format.Bytes(bytes)),
        (_, { } bytes, LeftoverKind.Folder) => Format.Bytes(bytes),
        _ => string.Empty,
    };

    public string Modified => Node.Modified?.ToLocalTime().ToString("g", Format.Culture) ?? string.Empty;

    public string ScoreText => Leftover.Score.ToString(Format.Culture);

    public double ScoreFraction => Leftover.Score / 100.0;

    public string TierText => Strings.Current.Tier(Leftover.Tier);

    public bool IsSafe => Leftover.Tier == ConfidenceTier.Safe;

    public bool IsReview => Leftover.Tier == ConfidenceTier.Review;

    public bool IsLow => Leftover.Tier == ConfidenceTier.Low;

    public bool IsFile => Leftover.Kind is LeftoverKind.Folder or LeftoverKind.File;

    public ReasonList Reasons { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool? IsChecked { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    private string? ValueLabel => Node.Kind == LeftoverKind.RegistryValue
        ? Node.ValueName is { Length: > 0 } name ? name : Strings.Current.DefaultValue
        : null;

    public IEnumerable<LeftoverItem> Chosen() => IsChecked switch
    {
        true => [IsRoot ? Leftover : LeftoverTree.ToItem(Leftover, Node)],
        null => _children?.SelectMany(c => c.Chosen()) ?? [],
        _ => [],
    };

    public override string ToString() => FullPath;

    internal void SetChildren(List<LeftoverTreeRow> children) => _children = children;

    [RelayCommand]
    private Task Toggle() => _toggle(this);

    private static string Join(string? first, string? second) =>
        string.Join(" · ", new[] { first, second }.Where(p => !string.IsNullOrEmpty(p)));
}
