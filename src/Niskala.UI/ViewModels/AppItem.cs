using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.UI.Services;

namespace Niskala.UI.ViewModels;

public sealed partial class AppItem : ObservableObject
{
    private const int RecentDays = 30;
    private const long LargeBytes = 500L * 1024 * 1024;

    public AppItem(InstalledApp app)
    {
        App = app;
        PlateBrush = Format.Plate(app.Package?.LogoBackground);
    }

    public InstalledApp App { get; }

    public string Id => App.Id;

    public string Name => App.DisplayName ?? App.Id;

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public string Subtitle => string.Join(" · ", new[] { App.Publisher, App.DisplayVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool IsStore => App.Sources.HasFlag(AppSources.Appx);

    public bool IsWin32 => !IsStore;

    public bool IsBroken => App.IsOrphan || App.IsUninstallerMissing;

    public bool CanBatch => !App.IsProtected && (!App.IsOrphan || App.InstallerKind == InstallerKind.Steam);

    public bool IsRecent => App.InstallDate is { } date && date.DayNumber >= DateOnly.FromDateTime(DateTime.Today).DayNumber - RecentDays;

    public bool IsLarge => SizeForSort >= LargeBytes;

    public string SourceText => IsStore ? "Store" : App.Sources.HasFlag(AppSources.Msi) ? "MSI" : "Win32";

    public string InstalledText => Format.Date(App.InstallDate);

    public string? MeasurableFolder => App.InstallLocationGuard == PathVerdict.Allowed && !string.IsNullOrEmpty(App.InstallLocation) ? App.InstallLocation : null;

    public SolidColorBrush? PlateBrush { get; }

    public bool HasIcon => Icon is not null;

    public long? SizeForSort => MeasuredBytes ?? App.EstimatedSizeBytes;

    public string SizeText => MeasuredBytes is { } measured ? Format.Bytes(measured)
        : App.EstimatedSizeBytes is { } estimate ? "≈ " + Format.Bytes(estimate)
        : SizePending ? "…"
        : string.Empty;

    public bool IconRequested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    public partial ImageSource? Icon { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(SizeForSort), nameof(IsLarge))]
    public partial long? MeasuredBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial bool SizePending { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
