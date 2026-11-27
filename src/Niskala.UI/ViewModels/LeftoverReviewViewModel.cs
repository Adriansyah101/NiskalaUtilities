using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Scanner.Leftovers;
using Niskala.UI.Services;
using Niskala.Uninstall.Leftovers;

namespace Niskala.UI.ViewModels;

public enum ReviewTab
{
    Files,
    Registry,
}

public enum ReviewScope
{
    All,
    Files,
    Registry,
}

public sealed partial class LeftoverReviewViewModel : ObservableObject
{
    private List<LeftoverTreeRow> _roots = [];
    private bool _cascading;

    [ObservableProperty]
    public partial LeftoverTreeRowCollection Visible { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial LeftoverTreeRow? Selected { get; set; }

    [ObservableProperty]
    public partial ReviewTab Tab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTabs), nameof(ShowPermanent), nameof(ScopeTitle))]
    public partial ReviewScope Scope { get; private set; }

    [ObservableProperty]
    public partial bool ShowSafe { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowReview { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowLow { get; set; }

    [ObservableProperty]
    public partial bool Permanent { get; set; }

    [ObservableProperty]
    public partial string CheckedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int CheckedCount { get; set; }

    public int SafeCount => _roots.Count(r => r.IsSafe);

    public int ReviewCount => _roots.Count(r => r.IsReview);

    public int LowCount => _roots.Count(r => r.IsLow);

    public int FileCount => _roots.Count(r => r.IsFile);

    public int RegistryCount => _roots.Count(r => !r.IsFile);

    public int Total => _roots.Count;

    public string FilesTabText => $"{S.TreeFiles}  {FileCount.ToString(Format.Culture)}";

    public string RegistryTabText => $"{S.TreeRegistry}  {RegistryCount.ToString(Format.Culture)}";

    public bool HasSelection => Selected is not null;

    public bool ShowTabs => Scope == ReviewScope.All;

    public bool ShowPermanent => Scope != ReviewScope.Registry;

    public string ScopeTitle => Scope == ReviewScope.Registry ? RegistryTabText : FilesTabText;

    public Strings S => Strings.Current;

    public IReadOnlyList<LeftoverItem> Checked => [.. _roots.SelectMany(r => r.Chosen())];

    public void Load(IEnumerable<LeftoverItem> items, Func<LeftoverItem, bool> preselect, ReviewScope scope = ReviewScope.All)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(preselect);
        foreach (LeftoverTreeRow row in Rows(_roots))
        {
            row.PropertyChanged -= OnRowChanged;
        }

        IEnumerable<LeftoverItem> shown = scope switch
        {
            ReviewScope.Files => items.Where(i => LeftoverStages.IsFile(i.Kind)),
            ReviewScope.Registry => items.Where(i => !LeftoverStages.IsFile(i.Kind)),
            _ => items,
        };
        _roots = [.. shown.Select(i => Watch(new LeftoverTreeRow(i, LeftoverTree.Root(i), null, preselect(i), ToggleAsync)))];
        Scope = scope;
        ShowLow = false;
        Permanent = false;
        Tab = scope switch
        {
            ReviewScope.Files => ReviewTab.Files,
            ReviewScope.Registry => ReviewTab.Registry,
            _ => FileCount > 0 || RegistryCount == 0 ? ReviewTab.Files : ReviewTab.Registry,
        };
        OnPropertyChanged(string.Empty);
        Refilter();
        UpdateChecked();
    }

    [RelayCommand]
    private void CheckVisible() => SetShown(true);

    [RelayCommand]
    private void ClearVisible() => SetShown(false);

    partial void OnTabChanged(ReviewTab value) => Refilter();

    partial void OnShowSafeChanged(bool value) => Refilter();

    partial void OnShowReviewChanged(bool value) => Refilter();

    partial void OnShowLowChanged(bool value) => Refilter();

    private void SetShown(bool value)
    {
        foreach (LeftoverTreeRow row in Visible.Where(r => r.IsRoot).ToList())
        {
            row.IsChecked = value;
        }
    }

    private async Task ToggleAsync(LeftoverTreeRow row)
    {
        if (!row.CanExpand)
        {
            return;
        }

        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            int index = Visible.IndexOf(row);
            while (index >= 0 && index + 1 < Visible.Count && Visible[index + 1].Depth > row.Depth)
            {
                Visible.RemoveAt(index + 1);
            }

            return;
        }

        if (row.Children is null)
        {
            LeftoverNode node = row.Node;
            LeftoverChildren children = await Task.Run(() => LeftoverTree.Children(node));
            bool inherited = row.IsChecked ?? false;
            row.SetChildren([.. children.Nodes.Select(n => Watch(new LeftoverTreeRow(row.Leftover, n, row, inherited, ToggleAsync)))]);
            row.Note = children.Error is { } error ? S.TreeUnreadable(error)
                : children.Truncated ? S.TreeTruncated(LeftoverTree.MaxChildren)
                : string.Empty;
        }

        row.IsExpanded = true;
        int at = Visible.IndexOf(row);
        if (at < 0)
        {
            return;
        }

        foreach (LeftoverTreeRow child in Shown(row.Children!))
        {
            Visible.Insert(++at, child);
        }
    }

    private void Refilter()
    {
        Visible = [.. Shown(_roots.Where(r =>
            ((ShowSafe && r.IsSafe) || (ShowReview && r.IsReview) || (ShowLow && r.IsLow))
            && r.IsFile == (Tab == ReviewTab.Files)))];
        if (Selected is null || !Visible.Contains(Selected))
        {
            Selected = Visible.FirstOrDefault();
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LeftoverTreeRow.IsChecked) || _cascading || sender is not LeftoverTreeRow row)
        {
            return;
        }

        _cascading = true;
        try
        {
            if (row.IsChecked is { } value)
            {
                foreach (LeftoverTreeRow below in Rows(row.Children ?? []))
                {
                    below.IsChecked = value;
                }
            }

            for (LeftoverTreeRow? parent = row.Parent; parent is not null; parent = parent.Parent)
            {
                parent.IsChecked = LeftoverTree.Combine(parent.Children!.Select(c => c.IsChecked));
            }
        }
        finally
        {
            _cascading = false;
        }

        UpdateChecked();
    }

    private void UpdateChecked()
    {
        IReadOnlyList<LeftoverItem> chosen = Checked;
        CheckedCount = chosen.Count;
        long bytes = chosen.Sum(i => i.Bytes ?? 0);
        CheckedText = S.SelectedItems(chosen.Count, _roots.Count(r => r.IsChecked != false), _roots.Count, bytes > 0 ? Format.Bytes(bytes) : null);
    }

    private LeftoverTreeRow Watch(LeftoverTreeRow row)
    {
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private static IEnumerable<LeftoverTreeRow> Shown(IEnumerable<LeftoverTreeRow> rows)
    {
        foreach (LeftoverTreeRow row in rows)
        {
            yield return row;
            if (row.IsExpanded && row.Children is { } children)
            {
                foreach (LeftoverTreeRow child in Shown(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<LeftoverTreeRow> Rows(IEnumerable<LeftoverTreeRow> rows)
    {
        foreach (LeftoverTreeRow row in rows)
        {
            yield return row;
            foreach (LeftoverTreeRow child in Rows(row.Children ?? []))
            {
                yield return child;
            }
        }
    }
}
