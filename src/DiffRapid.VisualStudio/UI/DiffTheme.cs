using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Shared look of the DiffRapid surfaces: the accent palette for difference kinds and flat, theme-aware
/// command-bar controls. Everything except the accents follows the active Visual Studio theme.
/// </summary>
internal static class DiffTheme
{
    public static readonly Brush ChangedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x1E)));
    public static readonly Brush AddedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)));
    public static readonly Brush RemovedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD7, 0x3A, 0x49)));
    public static readonly Brush MovedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    public static readonly Brush IgnoredBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80)));

    /// <summary>A translucent version of an accent, for pills and soft backgrounds.</summary>
    public static Brush Tint(Brush accent, byte alpha = 0x33)
    {
        var color = accent is SolidColorBrush solid ? solid.Color : Colors.Gray;
        return Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
    }

    private static readonly Lazy<Style> FlatButton = new(() => CreateFlatStyle(typeof(Button)));
    private static readonly Lazy<Style> FlatToggle = new(() => CreateFlatStyle(typeof(ToggleButton)));

    /// <summary>A borderless command-bar button that highlights on hover like the VS toolbars.</summary>
    public static Button CommandButton(object content, string tooltip, Action onClick)
    {
        var button = new Button { Content = content, ToolTip = tooltip, Style = FlatButton.Value };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A pill-shaped on/off option for the command bar.</summary>
    public static ToggleButton OptionToggle(string text, string tooltip, Action<bool> onChanged)
    {
        var toggle = new ToggleButton { Content = text, ToolTip = tooltip, Style = FlatToggle.Value, Margin = new Thickness(2, 0, 0, 0) };
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        return toggle;
    }

    public static FrameworkElement Separator()
    {
        var line = new Border { Width = 1, Margin = new Thickness(8, 5, 8, 5) };
        line.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarToolBarSeparatorBrushKey);
        return line;
    }

    /// <summary>A small caption such as "IGNORE" in the command bar.</summary>
    public static TextBlock Caption(string text)
    {
        var caption = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.75,
        };
        return caption;
    }

    /// <summary>A coloured dot followed by a value, for the difference summary.</summary>
    public static (FrameworkElement Element, TextBlock Value) Metric(Brush accent, string tooltip)
    {
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = accent, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), ToolTip = tooltip };
        panel.Children.Add(dot);
        panel.Children.Add(value);
        return (panel, value);
    }

    private static Style CreateFlatStyle(Type targetType)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        border.SetBinding(Border.BackgroundProperty, FromTemplate(Control.BackgroundProperty));
        border.SetBinding(Border.BorderBrushProperty, FromTemplate(Control.BorderBrushProperty));
        border.SetBinding(Border.PaddingProperty, FromTemplate(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);

        var style = new Style(targetType);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(targetType) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarTextActiveBrushKey)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 24.0));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey)));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarBorderBrushKey)));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarTextHoverBrushKey)));
        style.Triggers.Add(hover);

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarSelectedBorderBrushKey)));
        style.Triggers.Add(focus);

        if (typeof(ToggleButton).IsAssignableFrom(targetType))
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarSelectedBrushKey)));
            on.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarSelectedBorderBrushKey)));
            on.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarTextSelectedBrushKey)));
            style.Triggers.Add(on);
        }

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMouseDownBackgroundBeginBrushKey)));
        style.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarTextInactiveBrushKey)));
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6));
        style.Triggers.Add(disabled);

        style.Seal();
        return style;
    }

    private static Binding FromTemplate(DependencyProperty property) =>
        new(property.Name) { RelativeSource = RelativeSource.TemplatedParent };

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
