using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;

namespace DiffRapid.VisualStudio.UI;

/// <summary>What the selection dialog picks.</summary>
internal enum ComparisonTarget
{
    Files,
    Folders,

    /// <summary>Three files and a result file for a three-way merge.</summary>
    Merge,
}

/// <summary>Lets the user pick the left and right files (or folders) in one place.</summary>
internal sealed class SelectFilesDialog : DialogWindow
{
    private const int MaxPath = 32767;

    private static readonly string?[] LastLeft = new string?[3];
    private static readonly string?[] LastRight = new string?[3];
    private static string? _lastBase;
    private static string? _lastResult;

    private readonly ComparisonTarget _target;
    private readonly TextBox _left = PathBox();
    private readonly TextBox _right = PathBox();
    private readonly TextBox _base = PathBox();
    private readonly TextBox _result = PathBox();
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _compare;
    private bool _resultFollowsLeft;

    public SelectFilesDialog(ComparisonTarget target, string? left = null, string? right = null)
    {
        _target = target;
        var noun = Folders ? "folder" : "file";

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

        _left.Text = left ?? LastLeft[(int)target] ?? string.Empty;
        _right.Text = right ?? LastRight[(int)target] ?? string.Empty;
        _base.Text = _lastBase ?? string.Empty;
        // The result starts as the left file (what Save writes over), unless this is the same merge as last time.
        var sameMerge = !string.IsNullOrEmpty(_lastResult) && string.Equals(Normalize(_left.Text), LastLeft[(int)target], StringComparison.OrdinalIgnoreCase);
        _result.Text = sameMerge ? _lastResult! : _left.Text;
        _resultFollowsLeft = !sameMerge;
        _error.SetResourceReference(TextBlock.ForegroundProperty, ThemedDialogColors.ValidationErrorTextBrushKey);

        var grid = new Grid { Margin = new Thickness(20, 0, 20, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 5; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var row = 0;
        AddRow(grid, row++, "_Left:", _left, $"Select the left {noun}");
        if (Merging)
        {
            AddRow(grid, row++, "_Base:", _base, "Select the common ancestor of the left and right files");
        }

        AddRow(grid, row++, "_Right:", _right, $"Select the right {noun}");
        if (Merging)
        {
            AddRow(grid, row++, "Res_ult:", _result, "Choose where to save the merged file");
            _result.ToolTip = "Where Save writes the merged file. It starts as the left file; choose another file to leave the left one unchanged.";
        }

        Grid.SetRow(_error, row);
        Grid.SetColumn(_error, 1);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var swap = DialogButton("_Swap", (_, _) => (_left.Text, _right.Text) = (_right.Text, _left.Text));
        swap.ToolTip = $"Exchange the left and right {noun}s";
        _compare = DialogButton(Merging ? "_Merge" : "_Compare", (_, _) => Accept());
        _compare.IsDefault = true;
        var cancel = DialogButton("Cancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        buttons.Children.Add(swap);
        buttons.Children.Add(_compare);
        buttons.Children.Add(cancel);
        var footerRow = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Add(buttons);
        var hint = new TextBlock { Text = $"Tip: drag a {noun} from Explorer onto either box.", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        footerRow.Children.Add(hint);

        var footer = new Border { Child = footerRow, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        footer.SetResourceReference(Border.BorderBrushProperty, ThemedDialogColors.WindowBorderBrushKey);

        var heading = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        heading.Children.Add(new TextBlock { Text = Merging ? "Merge files" : Folders ? "Compare folders" : "Compare files", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock
        {
            Text = Merging
                ? "Choose two changed versions of a file and the base they both started from. Changes made on one side are taken automatically; you pick a side for each conflict."
                : Folders
                ? "Choose the two folders to compare. Results appear while the scan runs; double-click a file to compare it."
                : "Choose the two files to compare side by side. Unsaved edits in open documents are included.",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(subtitle);

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        layout.Children.Add(grid);
        Content = layout;
        _left.TextChanged += (_, _) =>
        {
            if (_resultFollowsLeft)
            {
                _result.Text = _left.Text;
            }

            Validate(showErrors: false);
        };
        _result.TextChanged += (_, _) => _resultFollowsLeft = _result.Text.Length == 0 || _result.Text == _left.Text;
        _right.TextChanged += (_, _) => Validate(showErrors: false);
        _base.TextChanged += (_, _) => Validate(showErrors: false);
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

    public string BasePath => Normalize(_base.Text);

    /// <summary>Where the merge is saved: the result box, or the left file when it is empty.</summary>
    public string ResultPath => Normalize(_result.Text) is { Length: > 0 } result ? result : LeftPath;

    private bool Folders => _target == ComparisonTarget.Folders;

    private bool Merging => _target == ComparisonTarget.Merge;

    private static TextBox PathBox()
    {
        var box = new TextBox { Margin = new Thickness(0, 0, 6, 8), MinHeight = 26, AllowDrop = true, VerticalContentAlignment = VerticalAlignment.Center };
        box.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);

        // Accept a file or folder dragged from Explorer or Solution Explorer.
        box.PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        box.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            {
                box.Text = paths[0];
                e.Handled = true;
            }
        };
        return box;
    }

    private static string Normalize(string path) => path.Trim().Trim('"');

    private bool Exists(string path) => Folders ? Directory.Exists(path) : File.Exists(path);

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
            var chosen = Folders ? BrowseForFolder(browseTitle, start)
                : ReferenceEquals(box, _result) ? BrowseForResult(browseTitle, start, current)
                : BrowseForFile(browseTitle, start, current);
            if (chosen is not null)
            {
                box.Text = chosen;
            }
        });
        browse.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(browse, row);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);
    }

    private string? BrowseForFile(string title, string? start, string current)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*",
            InitialDirectory = start ?? string.Empty,
            FileName = File.Exists(current) ? Path.GetFileName(current) : string.Empty,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    // The merge result may not exist yet, so it is chosen with a save dialog.
    private string? BrowseForResult(string title, string? start, string current)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "All files (*.*)|*.*",
            InitialDirectory = start ?? SafeDirectory(LeftPath) ?? string.Empty,
            FileName = Path.GetFileName(current.Length > 0 ? current : LeftPath),
            OverwritePrompt = false,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    // Visual Studio's own folder picker, so it matches the rest of the IDE.
    private string? BrowseForFolder(string title, string? start)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell)
        {
            return null;
        }

        var buffer = Marshal.AllocCoTaskMem(MaxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var info = new VSBROWSEINFOW
            {
                lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW)),
                hwndOwner = new WindowInteropHelper(this).Handle,
                pwzDlgTitle = title,
                pwzInitialDir = start ?? string.Empty,
                pwzDirName = buffer,
                nMaxDirName = MaxPath,
            };
            return shell.GetDirectoryViaBrowseDlg(new[] { info }) == VSConstants.S_OK ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
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
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool Validate(bool showErrors)
    {
        var noun = Folders ? "folder" : "file";
        string? problem = null;
        if (LeftPath.Length == 0 || RightPath.Length == 0)
        {
            problem = $"Choose a {noun} for both sides.";
        }
        else if (!Exists(LeftPath))
        {
            problem = $"The left {noun} does not exist.";
        }
        else if (!Exists(RightPath))
        {
            problem = $"The right {noun} does not exist.";
        }
        else if (Merging && BasePath.Length == 0)
        {
            problem = "Choose the base file both sides started from.";
        }
        else if (Merging && !File.Exists(BasePath))
        {
            problem = "The base file does not exist.";
        }
        else if (Merging && SafeDirectory(ResultPath) is null)
        {
            problem = "The folder for the result file does not exist.";
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

        LastLeft[(int)_target] = LeftPath;
        LastRight[(int)_target] = RightPath;
        if (Merging)
        {
            _lastBase = BasePath;
            _lastResult = Normalize(_result.Text);
        }

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
