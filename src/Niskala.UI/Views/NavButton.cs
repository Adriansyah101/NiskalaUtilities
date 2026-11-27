using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Niskala.UI.Services;

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

    public static readonly DependencyProperty HasGuideProperty =
        DependencyProperty.Register(nameof(HasGuide), typeof(bool), typeof(NavButton), new PropertyMetadata(false, OnChanged));

    private UIElement? _pill;

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

    public bool HasGuide
    {
        get => (bool)GetValue(HasGuideProperty);
        set => SetValue(HasGuideProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _pill = GetTemplateChild("Pill") as UIElement;
        if (_pill is not null)
        {
            _pill.ScaleTransition = new Vector3Transition { Duration = NavMotion.Press };
        }

        Refresh(false);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        Press(true);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        Press(false);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Press(false);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        Press(false);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        Press(false);
    }

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((NavButton)sender).Refresh(true);

    private void Press(bool pressed)
    {
        if (_pill is null)
        {
            return;
        }

        float scale = NavMotion.PressScale(pressed, Motion.Enabled);
        _pill.CenterPoint = new Vector3(_pill.ActualSize.X / 2, _pill.ActualSize.Y / 2, 0);
        _pill.Scale = new Vector3(scale, scale, 1);
    }

    private void Refresh(bool animate)
    {
        if (Label.Length > 0)
        {
            AutomationProperties.SetName(this, Label);
            ToolTipService.SetToolTip(this, IsCompact ? Label : null);
        }

        bool transitions = animate && Motion.Enabled;
        VisualStateManager.GoToState(this, NavMotion.EmphasisState(IsActive, IsOpen), transitions);
        VisualStateManager.GoToState(this, NavMotion.WidthState(IsCompact), transitions);
        VisualStateManager.GoToState(this, NavMotion.GuideState(HasGuide), false);
    }
}
