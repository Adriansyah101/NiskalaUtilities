using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niskala.Guardian.Journal;
using Niskala.Toolkit.Duplicates;
using Niskala.Toolkit.Storage;
using Niskala.Toolkit.Wipe;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed record StorageRow(StorageNode Node, string Size, double Percent, string Counts);

public sealed record ExtensionRow(string Extension, string Size, double Percent, string Files);

public sealed record DuplicateRow(bool IsHeader, string Title, string Detail, string Mark, bool IsRemoval);

public sealed partial class StorageViewModel : TaskViewModel
{
    private const int ExtensionsShown = 24;

    private readonly Stack<StorageNode> _trail = new();

    internal StorageViewModel(UiServices services)
        : base(services)
    {
    }

    public StorageRowCollection Children { get; } = [];

    public ExtensionRowCollection Extensions { get; } = [];

    [ObservableProperty]
    public partial string? Folder { get; set; }

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    public partial StorageNode? Current { get; set; }

    public bool CanGoUp => _trail.Count > 0;

    [RelayCommand]
    private void Browse()
    {
        if (Pickers.ScanFolder() is { } folder)
        {
            Folder = folder;
        }
    }

    [RelayCommand]
    private Task MapAsync() => Folder is not { } folder ? Task.CompletedTask : RunAsync(async token =>
    {
        var progress = new UiProgress<StorageProgress>(p => Status = S.Mapping(p.Folders, p.Files, Format.Bytes(p.Bytes)));
        Status = S.Mapping(0, 0, Format.Bytes(0));
        StorageMap map = await Task.Run(() => StorageMapper.MapAsync(folder, null, progress, token), token);
        _trail.Clear();
        long total = Math.Max(1, map.Root.Bytes);
        Extensions.Clear();
        foreach (ExtensionGroup group in map.Extensions.Take(ExtensionsShown))
        {
            Extensions.Add(new ExtensionRow(group.Extension, Format.Bytes(group.Bytes), group.Bytes * 100.0 / total, S.FileCount(group.Files)));
        }

        Show(map.Root);
        Info = (map.Inaccessible + map.Links > 0 ? S.MapSkipped(map.Inaccessible, map.Links) : null) ?? string.Empty;
    });

    [RelayCommand]
    private void Open(StorageRow? row)
    {
        if (row is not null && Current is { } current && row.Node.Children.Count > 0)
        {
            _trail.Push(current);
            Show(row.Node);
        }
    }

    [RelayCommand]
    private void Up()
    {
        if (_trail.TryPop(out StorageNode? parent))
        {
            Show(parent);
        }
    }

    [RelayCommand]
    private void Reveal(StorageRow? row)
    {
        if (row is not null)
        {
            int error = ShellLauncher.ShowItem(row.Node.Path);
            Error = (error == 0 ? null : S.OpenFailed(error)) ?? string.Empty;
        }
    }

    private void Show(StorageNode node)
    {
        Current = node;
        Heading = S.MapHeading(node.Path, Format.Bytes(node.Bytes), node.Files, node.Folders);
        long total = Math.Max(1, node.Bytes);
        Children.Clear();
        foreach (StorageNode child in node.Children)
        {
            Children.Add(new StorageRow(child, Format.Bytes(child.Bytes), child.Bytes * 100.0 / total, S.FileCount(child.Files)));
        }

        OnPropertyChanged(nameof(CanGoUp));
    }
}

public sealed partial class DuplicatesViewModel : TaskViewModel
{
    private DuplicateReport? _report;

    internal DuplicatesViewModel(UiServices services)
        : base(services)
    {
    }

    public PathCollection Folders { get; } = [];

    public DuplicateRowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial int KeepIndex { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial int Planned { get; set; }

    public bool CanRemove => Planned > 0;

    private KeepRule Keep => KeepIndex == 1 ? KeepRule.Newest : KeepRule.Oldest;

    partial void OnKeepIndexChanged(int value) => Show();

    [RelayCommand]
    private void AddFolder()
    {
        if (Pickers.ScanFolder() is { } folder && !Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder is not null)
        {
            Folders.Remove(folder);
        }
    }

    [RelayCommand]
    private Task ScanAsync() => Folders.Count == 0 ? Task.CompletedTask : RunAsync(async token =>
    {
        string[] roots = [.. Folders];
        var progress = new UiProgress<DuplicateProgress>(p => Status = S.DuplicateStage(p.Stage, p.Done, p.Total));
        DuplicateFinder finder = Services.Host.CreateDuplicateFinder();
        _report = await Task.Run(() => finder.FindAsync(roots, null, progress, token), token);
        Show();
    });

    [RelayCommand]
    private Task RemoveAsync() => _report is not { } report ? Task.CompletedTask : RunAsync(async token =>
    {
        Status = S.Removing;
        KeepRule keep = Keep;
        DuplicateCleaner cleaner = Services.Host.CreateDuplicateCleaner();
        DuplicateRemoval removal = await Task.Run(() => cleaner.RemoveAsync(report.Groups, keep, permanent: false, null, token), token);
        int removed = removal.Result?.Transaction.Items.Count(i => i.Status == RemovalStatus.Removed) ?? 0;
        Info = S.DuplicatesRemoved(removed, removal.Changed.Count, removal.Result?.Transaction.Id);
        Services.History.Invalidate();
        _report = null;
        Rows.Clear();
        Planned = 0;
        Summary = string.Empty;
    });

    private void Show()
    {
        Rows.Clear();
        if (_report is not { } report)
        {
            return;
        }

        foreach (DuplicateGroup group in report.Groups)
        {
            DuplicateFile keeper = DuplicateSelection.Keeper(group, Keep);
            Rows.Add(new DuplicateRow(true, S.DuplicateGroupTitle(Format.Bytes(group.Bytes), group.Files.Count), group.Hash[..16], string.Empty, false));
            foreach (DuplicateFile file in group.Files)
            {
                bool kept = ReferenceEquals(file, keeper);
                string mark = kept ? S.Keep : file.IsProtected ? S.Protected : S.RemoveMark;
                Rows.Add(new DuplicateRow(false, file.Path, Format.When(file.LastWriteUtc), mark, !kept && !file.IsProtected));
            }
        }

        IReadOnlyList<DuplicateFile> planned = DuplicateCleaner.Plan(report.Groups, Keep);
        Planned = planned.Count;
        Summary = S.DuplicateSummary(report.Scanned, report.Groups.Count, planned.Count, Format.Bytes(planned.Sum(f => f.Bytes)));
    }
}

public sealed partial class WipeViewModel : TaskViewModel
{
    internal WipeViewModel(UiServices services)
        : base(services)
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Drives.Add(drive.Name);
        }

    }

    public PathCollection Files { get; } = [];

    public PathCollection Drives { get; } = [];

    [ObservableProperty]
    public partial int MethodIndex { get; set; }

    [ObservableProperty]
    public partial int DriveIndex { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    public WipeMethod Method => (WipeMethod)Math.Clamp(MethodIndex, 0, 2);

    private string? SelectedDrive => DriveIndex >= 0 && DriveIndex < Drives.Count ? Drives[DriveIndex] : null;

    [RelayCommand]
    private void AddFile()
    {
        if (Pickers.AnyFile() is { } file && !Files.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            Files.Add(file);
        }
    }

    [RelayCommand]
    private void RemoveFile(string? file)
    {
        if (file is not null)
        {
            Files.Remove(file);
        }
    }

    [RelayCommand]
    private Task WipeFilesAsync() => Files.Count == 0 ? Task.CompletedTask : RunAsync(async token =>
    {
        string[] targets = [.. Files];
        WipeMethod method = Method;
        SecureWiper wiper = Services.Host.CreateSecureWiper();
        var progress = new UiProgress<WipeProgress>(Track);
        WipeResult[] results = await Task.Run(() => targets.Select(t => wiper.WipeFile(t, method, progress, token)).ToArray(), token);
        foreach (WipeResult result in results.Where(r => r.Outcome == WipeOutcome.Wiped))
        {
            Files.Remove(result.Path);
        }

        WipeResult[] failed = [.. results.Where(r => r.Outcome != WipeOutcome.Wiped)];
        Info = S.FilesWiped(results.Length - failed.Length, method);
        Error = (failed.Length == 0 ? null : string.Join(Environment.NewLine, failed.Select(f => $"{Path.GetFileName(f.Path)}: {S.WipeOutcomeText(f.Outcome)} {f.Detail}"))) ?? string.Empty;
        Percent = 0;
    });

    [RelayCommand]
    private Task WipeFreeSpaceAsync() => SelectedDrive is not { } drive ? Task.CompletedTask : RunAsync(async token =>
    {
        WipeMethod method = Method;
        SecureWiper wiper = Services.Host.CreateSecureWiper();
        var progress = new UiProgress<WipeProgress>(Track);
        FreeSpaceResult result = await Task.Run(() => wiper.WipeFreeSpaceAsync(drive, method, null, progress, token), token);
        Info = (result.Outcome == WipeOutcome.Wiped ? S.FreeSpaceWiped(drive, Format.Bytes(result.Bytes), result.Passes) : null) ?? string.Empty;
        Error = (result.Outcome == WipeOutcome.Wiped ? null : $"{S.WipeOutcomeText(result.Outcome)} {result.Detail}") ?? string.Empty;
        Percent = 0;
    });

    private void Track(WipeProgress progress)
    {
        Percent = progress.Total <= 0 ? 0 : ((progress.Pass - 1) + (progress.Written / (double)progress.Total)) * 100 / progress.Passes;
        Status = S.WipePass(progress.Pass, progress.Passes, Path.GetFileName(progress.Path), Format.Bytes(progress.Written));
    }
}
