using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DiffRapid.Core.Plugins;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Lets the user choose an unpacker and a prediffer pipeline for a comparison, such as
/// <c>PrettifyJson sort | SortLines</c>. The choice is kept for the rest of the session.
/// </summary>
internal sealed class PluginsDialog : DialogWindow
{
    private const string NoUnpacker = "(none)";

    private static string _lastUnpacker = PluginOptions.AutomaticUnpacker;
    private static string _lastPrediffer = string.Empty;

    private readonly PluginRegistry _registry = PluginRegistry.Default;
    private readonly ComboBox _unpacker = PipelineBox();
    private readonly ComboBox _prediffer = PipelineBox();
    private readonly TextBlock _unpackerHelp = HelpText();
    private readonly TextBlock _predifferHelp = HelpText();
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _compare;

    // The pipeline as typed, before picking from the list replaced the box's text; and whether a pick is being added.
    private string _lastTypedPrediffer = _lastPrediffer;
    private bool _appending;

    public PluginsDialog(string leftName, string rightName)
    {
        var opening = System.Diagnostics.Stopwatch.StartNew();
        ContentRendered += (_, _) => Diagnostics.LogIfSlow("Showing the Compare with plugins dialog", opening);
        Title = "DiffRapid";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        HasMinimizeButton = false;
        HasMaximizeButton = false;
        SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
        SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemedDialogColors.ValidationErrorTextBrushKey);

        _unpacker.Items.Add(NoUnpacker);
        _unpacker.Items.Add(PluginOptions.AutomaticUnpacker);
        foreach (var plugin in _registry.Unpackers)
        {
            _unpacker.Items.Add(plugin.Name);
        }

        foreach (var plugin in _registry.Prediffers)
        {
            _prediffer.Items.Add(plugin.Name);
        }

        _unpacker.Text = _lastUnpacker.Length == 0 ? NoUnpacker : _lastUnpacker;
        _prediffer.Text = _lastPrediffer;

        var heading = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        heading.Children.Add(new TextBlock { Text = "Compare with plugins", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock
        {
            Text = $"Turn {leftName} and {rightName} into text with an unpacker, then transform that text with prediffers before comparing. "
                + "The result opens in a new read-only comparison; the files themselves are not changed.",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(subtitle);

        var grid = new Grid { Margin = new Thickness(20, 0, 20, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddRow(grid, 0, "_Unpacker:", _unpacker, "'auto' picks one by file name; for example ZipEntries lists and compares the files inside a .zip");
        AddRow(grid, 1, null, _unpackerHelp, null);
        AddRow(grid, 2, "_Prediffers:", _prediffer, "Steps separated by |, each a plugin name and its arguments, such as: PrettifyJson sort | SortLines");
        AddRow(grid, 3, null, _predifferHelp, null);
        Grid.SetRow(_error, 4);
        Grid.SetColumn(_error, 1);
        grid.Children.Add(_error);

        _compare = DialogButton("_Compare", (_, _) => Accept());
        _compare.IsDefault = true;
        var cancel = DialogButton("Cancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_compare);
        buttons.Children.Add(cancel);
        var footer = new Border { Child = buttons, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        footer.SetResourceReference(Border.BorderBrushProperty, ThemedDialogColors.WindowBorderBrushKey);

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        layout.Children.Add(grid);
        Content = layout;

        _unpacker.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => ShowHelp()));
        _prediffer.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => ShowHelp()));
        // Once the list has closed, so the combo box does not put the picked name back over the composed pipeline.
        _prediffer.DropDownClosed += (_, _) => AppendPickedPrediffer();
        Loaded += (_, _) =>
        {
            Diagnostics.LogIfSlow("Loading the Compare with plugins dialog", opening);
            ShowHelp();
            _prediffer.Focus();
        };
        Diagnostics.LogIfSlow("Building the Compare with plugins dialog", opening);
    }

    /// <summary>The chosen pipelines; empty strings mean none.</summary>
    public PluginOptions Options => new()
    {
        Unpacker = UnpackerText.Length == 0 ? null : UnpackerText,
        Prediffer = _prediffer.Text.Trim().Length == 0 ? null : _prediffer.Text.Trim(),
    };

    private string UnpackerText => _unpacker.Text.Trim() is var text && text != NoUnpacker ? text : string.Empty;

    /// <summary>
    /// Picking a prediffer from the list adds it as a new step after what is typed, so pipelines can be built from the
    /// list without knowing the syntax.
    /// </summary>
    private void AppendPickedPrediffer()
    {
        if (_appending || _prediffer.SelectedItem is not string picked)
        {
            return;
        }

        _appending = true;
        var typed = _lastTypedPrediffer.Trim();
        var text = typed.Length == 0 || typed == picked ? picked : typed + " | " + picked;

        // A plugin with options (PrettifyJson sort, SortLines desc) leaves the caret after a space, ready for them.
        if (_registry.FindPrediffer(picked) is { ArgumentsHelp.Length: > 0 })
        {
            text += " ";
        }
        _prediffer.SelectedIndex = -1;
        _prediffer.Text = text;
        _lastTypedPrediffer = text;
        if (_prediffer.Template.FindName("PART_EditableTextBox", _prediffer) is TextBox box)
        {
            box.CaretIndex = text.Length;
        }

        _appending = false;
    }

    private void ShowHelp()
    {
        if (!_appending && _prediffer.SelectedIndex < 0)
        {
            _lastTypedPrediffer = _prediffer.Text;
        }

        _unpackerHelp.Text = UnpackerText.Length == 0
            ? "Files are read as text."
            : UnpackerText.Equals(PluginOptions.AutomaticUnpacker, StringComparison.OrdinalIgnoreCase)
            ? "Chosen by file name: " + string.Join(", ", _registry.Unpackers.Select(plugin => plugin.Name)) + ". Other files are read as text."
            : Describe(UnpackerText, name => (IPlugin?)_registry.FindUnpacker(name) ?? _registry.FindPrediffer(name));
        _predifferHelp.Text = _prediffer.Text.Trim().Length == 0
            ? "Pick a prediffer from the list to add it as a step. " + _registry.Prediffers.Count + " are built in."
            : Describe(_prediffer.Text, name => _registry.FindPrediffer(name));
        _error.Text = string.Empty;
    }

    /// <summary>One line per step: what its plugin does and the arguments it takes.</summary>
    private static string Describe(string pipeline, Func<string, IPlugin?> find)
    {
        IReadOnlyList<PluginStep> steps;
        try
        {
            steps = PluginPipelineParser.Parse(pipeline);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }

        var lines = new List<string>();
        foreach (var step in steps)
        {
            var plugin = find(step.Name);
            if (plugin is null)
            {
                lines.Add($"{step.Name}: not a known plugin.");
                continue;
            }

            var arguments = plugin.ArgumentsHelp.Length == 0 ? string.Empty : $" Options to type after the name: {plugin.ArgumentsHelp}";
            lines.Add($"{plugin.Name}: {plugin.Description}{arguments}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void Accept()
    {
        var error = Check(UnpackerText, "unpacker", name => (IPlugin?)_registry.FindUnpacker(name) ?? _registry.FindPrediffer(name))
            ?? Check(_prediffer.Text, "prediffer", name => _registry.FindPrediffer(name));
        if (UnpackerText.Length == 0 && _prediffer.Text.Trim().Length == 0)
        {
            error = "Choose an unpacker or at least one prediffer.";
        }

        if (error is not null)
        {
            _error.Text = error;
            return;
        }

        _lastUnpacker = UnpackerText;
        _lastPrediffer = _prediffer.Text.Trim();
        DialogResult = true;
    }

    private static string? Check(string pipeline, string kind, Func<string, IPlugin?> find)
    {
        if (pipeline.Trim().Length == 0 || pipeline.Trim().Equals(PluginOptions.AutomaticUnpacker, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var unknown = PluginPipelineParser.Parse(pipeline).FirstOrDefault(step => find(step.Name) is null);
            return unknown is null ? null : $"There is no {kind} named '{unknown.Name}'.";
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private static ComboBox PipelineBox()
    {
        var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, Margin = new Thickness(0, 0, 0, 4), MinHeight = 26 };
        box.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        return box;
    }

    private static TextBlock HelpText()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return text;
    }

    private static void AddRow(Grid grid, int row, string? label, FrameworkElement content, string? tooltip)
    {
        if (label is not null)
        {
            var caption = new Label { Content = label, Target = content, Padding = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            caption.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
            Grid.SetRow(caption, row);
            grid.Children.Add(caption);
        }

        content.ToolTip = tooltip;
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
    }

    private static Button DialogButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
        button.Click += onClick;
        return button;
    }
}
