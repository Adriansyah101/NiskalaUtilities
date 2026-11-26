using Microsoft.UI.Xaml.Controls;

namespace Niskala.UI.Views;

public sealed partial class NiskalaMark : UserControl
{
    public NiskalaMark() => InitializeComponent();

    public Task PlayIntroAsync() => Intro.RunAsync();
}
