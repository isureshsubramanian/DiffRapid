using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
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
    /// <summary>A flat on/off button; <paramref name="isChecked"/> is its starting state and does not call <paramref name="onChanged"/>.</summary>
    public static ToggleButton OptionToggle(string text, string tooltip, Action<bool> onChanged, bool isChecked = false)
    {
        var toggle = new ToggleButton { Content = text, ToolTip = tooltip, Style = FlatToggle.Value, Margin = new Thickness(2, 0, 0, 0), IsChecked = isChecked };
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        return toggle;
    }

    /// <summary>
    /// Lays out a toolbar of two groups: <paramref name="left"/> and <paramref name="right"/> share one row while they
    /// fit, with <paramref name="right"/> at the far end; when the window is too narrow, <paramref name="right"/>
    /// moves to a second row instead of being cut off.
    /// </summary>
    public static void WrapWhenNarrow(Grid bar, FrameworkElement left, FrameworkElement right)
    {
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.HorizontalAlignment = HorizontalAlignment.Left;

        void Arrange()
        {
            var fits = left.DesiredSize.Width + right.DesiredSize.Width <= bar.ActualWidth;
            var row = fits ? 0 : 1;
            if (Grid.GetRow(right) != row)
            {
                Grid.SetRow(right, row);
                right.HorizontalAlignment = fits ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                right.Margin = fits ? default : new Thickness(0, 4, 0, 0);
            }
        }

        right.HorizontalAlignment = HorizontalAlignment.Right;
        bar.SizeChanged += (_, _) => Arrange();
        left.SizeChanged += (_, _) => Arrange();
        right.SizeChanged += (_, _) => Arrange();
    }

    /// <summary>
    /// Shows the column headers of a themed list: the Visual Studio list style has no header row, so once the list
    /// is loaded its scroll area takes the standard grid-view layout (header row above the items), and the headers
    /// get a flat, theme-aware look with a gripper for resizing.
    /// </summary>
    public static void ShowColumnHeaders(ListView list, GridView view)
    {
        view.ColumnHeaderContainerStyle = ColumnHeaderStyle.Value;
        list.Loaded += (_, _) =>
        {
            if (FindChild<GridViewHeaderRowPresenter>(list) is not null || FindChild<ScrollViewer>(list) is not { } scroller)
            {
                return;
            }

            if (list.TryFindResource(GridView.GridViewScrollViewerStyleKey) is Style gridScroller)
            {
                scroller.Style = gridScroller;
            }
        };
    }

    private static readonly Lazy<Style> ColumnHeaderStyle = new(() =>
    {
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 3, 6, 4));
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);

        // The named gripper is what GridViewColumnHeader uses for resizing.
        var gripper = new FrameworkElementFactory(typeof(Thumb), "PART_HeaderGripper");
        gripper.SetValue(FrameworkElement.WidthProperty, 8.0);
        gripper.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        gripper.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, -4, 0));
        gripper.SetValue(FrameworkElement.CursorProperty, Cursors.SizeWE);
        gripper.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(Thumb)) { VisualTree = TransparentBorder() });

        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.WidthProperty, 1.0);
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        line.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);

        var cell = new FrameworkElementFactory(typeof(Grid));
        cell.AppendChild(text);
        cell.AppendChild(line);
        cell.AppendChild(gripper);

        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        chrome.AppendChild(cell);

        var style = new Style(typeof(GridViewColumnHeader));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = chrome }));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.SystemGrayTextBrushKey)));
        style.Seal();
        return style;
    });

    private static FrameworkElementFactory TransparentBorder()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return border;
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Gives a slider the flat command-bar look: a thin track, the filled part and the thumb in the theme's accent.
    /// The template draws with the slider's own brushes, which follow the theme through resource references.
    /// </summary>
    public static void FlatSlider(Slider slider)
    {
        slider.Style = FlatSliderStyle.Value;
        slider.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemHighlightBrushKey);
        slider.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.CommandBarMenuSeparatorBrushKey);
    }

    private static readonly Lazy<Style> FlatSliderStyle = new(() => (Style)System.Windows.Markup.XamlReader.Parse(
        """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Slider">
                <ControlTemplate.Resources>
                  <Style x:Key="Part" TargetType="RepeatButton">
                    <Setter Property="Focusable" Value="False" />
                    <Setter Property="IsTabStop" Value="False" />
                    <Setter Property="Template">
                      <Setter.Value>
                        <ControlTemplate TargetType="RepeatButton">
                          <Border Background="Transparent">
                            <Border Height="2" VerticalAlignment="Center" CornerRadius="1" Background="{TemplateBinding Background}" />
                          </Border>
                        </ControlTemplate>
                      </Setter.Value>
                    </Setter>
                  </Style>
                </ControlTemplate.Resources>
                <Grid MinHeight="18" Background="Transparent">
                  <Track x:Name="PART_Track">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Style="{StaticResource Part}" Command="{x:Static Slider.DecreaseLarge}" Background="{TemplateBinding Foreground}" />
                    </Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Style="{StaticResource Part}" Command="{x:Static Slider.IncreaseLarge}" Background="{TemplateBinding BorderBrush}" />
                    </Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb Width="8" Height="14">
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Border CornerRadius="2" Background="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}" />
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """));

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
