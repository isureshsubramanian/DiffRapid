using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Lets the user pick the left and right files in one place.</summary>
internal sealed class SelectFilesDialog : DialogWindow
{
    private static string? _lastLeft;
    private static string? _lastRight;

    private readonly TextBox _left = PathBox();
    private readonly TextBox _right = PathBox();
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _compare;

    public SelectFilesDialog(string? left = null, string? right = null)
    {
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

        _left.Text = left ?? _lastLeft ?? string.Empty;
        _right.Text = right ?? _lastRight ?? string.Empty;
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemedDialogColors.ValidationErrorTextBrushKey);

        var grid = new Grid { Margin = new Thickness(20, 0, 20, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 4; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddRow(grid, 0, "_Left:", _left, "Select the left file");
        AddRow(grid, 1, "_Right:", _right, "Select the right file");

        Grid.SetRow(_error, 2);
        Grid.SetColumn(_error, 1);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var swap = DialogButton("_Swap", (_, _) => (_left.Text, _right.Text) = (_right.Text, _left.Text));
        swap.ToolTip = "Exchange the left and right files";
        _compare = DialogButton("_Compare", (_, _) => Accept());
        _compare.IsDefault = true;
        var cancel = DialogButton("Cancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        buttons.Children.Add(swap);
        buttons.Children.Add(_compare);
        buttons.Children.Add(cancel);
        var footerRow = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Add(buttons);
        var hint = new TextBlock { Text = "Tip: drag a file from Explorer onto either box.", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        footerRow.Children.Add(hint);

        var footer = new Border { Child = footerRow, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        footer.SetResourceReference(Border.BorderBrushProperty, ThemedDialogColors.WindowBorderBrushKey);

        var heading = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        heading.Children.Add(new TextBlock { Text = "Compare files", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = "Choose the two files to compare side by side. Unsaved edits in open documents are included.", Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(subtitle);

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        layout.Children.Add(grid);
        Content = layout;
        _left.TextChanged += (_, _) => Validate(showErrors: false);
        _right.TextChanged += (_, _) => Validate(showErrors: false);
        Loaded += (_, _) =>
        {
            Validate(showErrors: false);
            var focus = string.IsNullOrEmpty(_left.Text) ? _left : _right;
            focus.Focus();
            focus.SelectAll();
        };
    }

    public string LeftPath => Normalize(_left.Text);

    public string RightPath => Normalize(_right.Text);

    private static TextBox PathBox()
    {
        var box = new TextBox { Margin = new Thickness(0, 0, 6, 8), MinHeight = 26, AllowDrop = true, VerticalContentAlignment = VerticalAlignment.Center };
        box.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);

        // Accept a file dragged from Explorer or Solution Explorer.
        box.PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        box.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                box.Text = files[0];
                e.Handled = true;
            }
        };
        return box;
    }

    private static string Normalize(string path) => path.Trim().Trim('"');

    private void AddRow(Grid grid, int row, string label, TextBox box, string browseTitle)
    {
        var text = new Label { Content = label, Target = box, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var browse = DialogButton("Browse…", (_, _) =>
        {
            var current = Normalize(box.Text);
            var other = Normalize(ReferenceEquals(box, _left) ? _right.Text : _left.Text);
            var start = Directory.Exists(current) ? current : SafeDirectory(current) ?? SafeDirectory(other);
            var dialog = new OpenFileDialog
            {
                Title = browseTitle,
                CheckFileExists = true,
                Filter = "All files (*.*)|*.*",
                InitialDirectory = start ?? string.Empty,
                FileName = File.Exists(current) ? Path.GetFileName(current) : string.Empty,
            };
            if (dialog.ShowDialog(this) == true)
            {
                box.Text = dialog.FileName;
            }
        });
        browse.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(browse, row);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);
    }

    private static Button DialogButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
        button.Click += onClick;
        return button;
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : null;
        }
        catch (System.ArgumentException)
        {
            return null;
        }
    }

    private bool Validate(bool showErrors)
    {
        string? problem = null;
        if (LeftPath.Length == 0 || RightPath.Length == 0)
        {
            problem = "Choose a file for both sides.";
        }
        else if (!File.Exists(LeftPath))
        {
            problem = "The left file does not exist.";
        }
        else if (!File.Exists(RightPath))
        {
            problem = "The right file does not exist.";
        }

        _compare.IsEnabled = problem is null || !showErrors;
        _error.Text = showErrors ? problem ?? string.Empty : string.Empty;
        return problem is null;
    }

    private void Accept()
    {
        if (!Validate(showErrors: true))
        {
            return;
        }

        _lastLeft = LeftPath;
        _lastRight = RightPath;
        DialogResult = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
