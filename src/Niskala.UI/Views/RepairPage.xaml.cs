using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Niskala.UI.Services;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Views;

public sealed partial class LogLineTemplates : DataTemplateSelector
{
    public DataTemplate? Command { get; set; }

    public DataTemplate? Output { get; set; }

    public DataTemplate? Error { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => (item as LogLine)?.Kind switch
    {
        LogLineKind.Command => Command,
        LogLineKind.Error => Error,
        _ => Output,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed partial class RepairPage : UserControl
{
    public RepairPage(RepairViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        var events = new Attachment(() => ViewModel.LineAdded += ScrollToEnd, () => ViewModel.LineAdded -= ScrollToEnd);
        Loaded += (_, _) =>
        {
            events.Attach();
            ScrollToEnd();
        };
        Unloaded += (_, _) => events.Detach();
    }

    public RepairViewModel ViewModel { get; }

    private void ScrollToEnd()
    {
        if (ViewModel.Lines.Count > 0)
        {
            Log.ScrollIntoView(ViewModel.Lines[^1]);
        }
    }

    private void OnBannerClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Banner.Clear();

    private void OnPickSource(object sender, RoutedEventArgs e) => ViewModel.UseSource(Pickers.RepairImage());

    private void OnRetryWithSource(object sender, RoutedEventArgs e)
    {
        if (ViewModel.UseSource(Pickers.RepairImage()))
        {
            Confirm(RunButton);
        }
    }

    private void OnRun(object sender, RoutedEventArgs e) => Confirm(RunButton);

    private void Confirm(FrameworkElement anchor)
    {
        if (ViewModel.Selected is not { } row || !ViewModel.IsIdle)
        {
            return;
        }

        if (!row.Command.ChangesSystem)
        {
            ViewModel.RunCommand.Execute(null);
            return;
        }

        var confirm = new Button { Content = Strings.Current.RepairRun, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Width = 320,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Strings.Current.RepairConfirm(row.Name, ViewModel.SelectedCommandLine, row.Command.NeedsRestart), TextWrapping = TextWrapping.Wrap },
                    confirm,
                },
            },
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.RunCommand.Execute(null);
        };
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }
}
