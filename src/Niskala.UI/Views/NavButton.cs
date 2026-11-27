using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Niskala.UI.Views;

public sealed partial class NavButton : Button
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(NavButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(NavButton), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty TrailingGlyphProperty =
        DependencyProperty.Register(nameof(TrailingGlyph), typeof(string), typeof(NavButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(NavButton), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(NavButton), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(NavButton), new PropertyMetadata(false, OnChanged));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string TrailingGlyph
    {
        get => (string)GetValue(TrailingGlyphProperty);
        set => SetValue(TrailingGlyphProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Refresh(false);
    }

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((NavButton)sender).Refresh(true);

    private void Refresh(bool animate)
    {
        if (Label.Length > 0)
        {
            AutomationProperties.SetName(this, Label);
            ToolTipService.SetToolTip(this, IsCompact ? Label : null);
        }

        VisualStateManager.GoToState(this, IsActive ? "Active" : IsOpen ? "Open" : "Plain", animate);
        VisualStateManager.GoToState(this, IsCompact ? "Compact" : "Full", animate);
    }
}
