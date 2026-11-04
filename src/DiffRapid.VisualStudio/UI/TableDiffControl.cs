using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Table;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using TableColumn = DiffRapid.Core.Table.TableColumn;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Table comparison of two delimited text files (CSV, TSV...). One virtualized grid shows the aligned rows: a
/// status column, then every table column split into a left and a right half. Rows match by key columns when
/// given, otherwise by position with a line diff. The comparison runs in the background and runs again
/// whenever an option changes.
/// </summary>
internal sealed class TableDiffControl : DockPanel, IComparisonView
{
    private const double MinCellColumnWidth = 120;
    private const double MaxCellColumnWidth = 480;
    private const int WidthSampleRows = 300;
    private const int FooterDuplicateLimit = 3;

    private static readonly (string Label, char? Delimiter)[] Delimiters =
    {
        ("Auto", null),
        ("Comma", ','),
        ("Tab", '\t'),
        ("Semicolon", ';'),
        ("Pipe", '|'),
    };

    private readonly DiffRapidPackage _package;
    private readonly string _leftPath;
    private readonly string _rightPath;
    private readonly ListView _list = new();
    private readonly GridView _view = new();
    private readonly Dictionary<string, double> _columnWidths = new(StringComparer.Ordinal);
    private readonly ComboBox _delimiter = new() { MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, ToolTip = "The character between cells; Auto detects it from the files" };
    private readonly ToggleButton _headerRow;
    private readonly ToggleButton _ignoreCase;
    private readonly ToggleButton _trim;
    private readonly ToggleButton _showIdentical;
    private readonly TextBox _keyColumns = OptionBox(150, "Match rows by these columns: names or 1-based numbers, separated by commas (Enter applies). Empty matches rows by position.");
    private readonly TextBox _ignoreColumns = OptionBox(150, "Leave these columns out of the comparison: names or 1-based numbers, separated by commas (Enter applies)");
    private readonly TextBox _tolerance = OptionBox(64, "Numbers closer than this compare equal, e.g. 0.001; end with % for a relative tolerance, e.g. 1% (Enter applies)");
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _exportReport;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _leftInfo = new();
    private readonly TextBlock _rightInfo = new();
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };

    // The option text the shown comparison was made with; a box differs from it while the user is editing.
    private string _appliedKeys = string.Empty;
    private string _appliedIgnore = string.Empty;
    private string _appliedTolerance = string.Empty;

    private LoadedTables? _tables;
    private CompareOutcome? _outcome;
    private CancellationTokenSource? _compareCancellation;
    private bool _comparing;
    private bool _disposed;

    public TableDiffControl(DiffRapidPackage package, string leftPath, string rightPath)
    {
        _package = package;
        _leftPath = Path.GetFullPath(leftPath);
        _rightPath = Path.GetFullPath(rightPath);

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        _headerRow = DiffTheme.OptionToggle("Header row", "The first line of each file names the columns", _ => StartCompare(), isChecked: true);
        _ignoreCase = DiffTheme.OptionToggle("Ignore case", "Cells that differ only in letter case compare equal", _ => StartCompare());
        _trim = DiffTheme.OptionToggle("Trim", "Ignore spaces at the start and end of cells", _ => StartCompare());
        _showIdentical = DiffTheme.OptionToggle("Show identical rows", "Also list the rows that are the same on both sides", _ => ShowRows());
        _previous = DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous difference (Shift+F7)", () => NavigateDifference(-1));
        _next = DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next difference (F7)", () => NavigateDifference(1));
        _exportReport = DiffTheme.CommandButton(IconLabel(KnownMonikers.Report, "Export report"), "Save the differences as an HTML or Markdown report", ExportReport);

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var header = BuildHeader();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildList());

        PreviewKeyDown += OnPreviewKeyDown;
        VSColorTheme.ThemeChanged += OnThemeChanged;
        UpdateCommands();
        StartCompare();
    }

    public bool IsDirty => false;

    public void Save()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        _compareCancellation?.Cancel();
    }

    private FrameworkElement BuildToolbar()
    {
        var commands = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 2) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Compare again (F5)", StartCompare));
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_previous);
        actions.Children.Add(_next);
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_exportReport);
        actions.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.CompareFiles, "Open as text"), "Compare the two files line by line in the text view", OpenAsText));
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_verdict);
        SetDock(actions, Dock.Left);
        commands.Children.Add(actions);
        SetDock(_showIdentical, Dock.Right);
        commands.Children.Add(_showIdentical);

        var options = new WrapPanel { Margin = new Thickness(6, 2, 6, 4) };
        options.Children.Add(DiffTheme.Caption("DELIMITER"));
        _delimiter.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var (label, _) in Delimiters)
        {
            _delimiter.Items.Add(label);
        }

        _delimiter.SelectedIndex = 0;
        _delimiter.SelectionChanged += (_, _) => StartCompare();
        options.Children.Add(_delimiter);
        options.Children.Add(_headerRow);
        options.Children.Add(DiffTheme.Separator());
        options.Children.Add(DiffTheme.Caption("KEY"));
        options.Children.Add(_keyColumns);
        options.Children.Add(DiffTheme.Separator());
        options.Children.Add(DiffTheme.Caption("IGNORE"));
        options.Children.Add(_ignoreColumns);
        options.Children.Add(_ignoreCase);
        options.Children.Add(_trim);
        options.Children.Add(DiffTheme.Separator());
        options.Children.Add(DiffTheme.Caption("TOLERANCE"));
        options.Children.Add(_tolerance);
        foreach (var box in new[] { _keyColumns, _ignoreColumns, _tolerance })
        {
            box.KeyDown += OnOptionBoxKeyDown;
            box.LostKeyboardFocus += (_, _) => ApplyEditedOptions();
        }

        var stack = new DockPanel();
        SetDock(commands, Dock.Top);
        stack.Children.Add(commands);
        stack.Children.Add(options);

        var chrome = new Border { Child = stack, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        chrome.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.CommandBarGradientBeginColorKey);
        return chrome;
    }

    private static TextBox OptionBox(double width, string tooltip)
    {
        var box = new TextBox { Width = width, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip };
        box.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
        return box;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = FileLabel("LEFT", _leftPath, DiffTheme.RemovedBrush, _leftInfo);
        var right = FileLabel("RIGHT", _rightPath, DiffTheme.AddedBrush, _rightInfo);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var chrome = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 7, 10, 7) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    // A coloured side badge, the file path and (after a comparison) the size of the table in grey.
    private static FrameworkElement FileLabel(string side, string path, Brush accent, TextBlock info)
    {
        var badge = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = side, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        };
        info.Margin = new Thickness(10, 0, 0, 0);
        info.VerticalAlignment = VerticalAlignment.Center;
        info.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        var text = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel { ToolTip = path, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(badge, Dock.Left);
        DockPanel.SetDock(info, Dock.Right);
        row.Children.Add(badge);
        row.Children.Add(info);
        row.Children.Add(text);
        return row;
    }

    private FrameworkElement BuildFooter()
    {
        var panel = new DockPanel { Margin = new Thickness(10, 3, 10, 3) };
        _position.Margin = new Thickness(0, 0, 20, 0);
        SetDock(_position, Dock.Left);
        panel.Children.Add(_position);
        panel.Children.Add(_details);

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return chrome;
    }

    // A ListView with a GridView: only the visible rows get containers, and those are recycled while scrolling.
    private UIElement BuildList()
    {
        _list.View = _view;
        DiffTheme.ShowColumnHeaders(_list, _view);
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode = SelectionMode.Single;
        _list.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogListViewStyleKey);
        _list.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.ToolWindowBackgroundColorKey);
        _list.Loaded += (_, _) => ApplyListStyles();
        _list.SelectionChanged += (_, _) => UpdatePosition();
        ScrollViewer.SetCanContentScroll(_list, true);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Item);
        return _list;
    }

    // The themed item style is extended so cells stretch across their column (their tint then fills it). It is
    // looked up again when the theme changes, because the derived style holds on to the old theme's base.
    private void ApplyListStyles()
    {
        var itemStyle = new Style(typeof(ListViewItem), _list.TryFindResource(VsResourceKeys.ThemedDialogListViewItemGridStyleKey) as Style);
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = itemStyle;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!_disposed)
            {
                ApplyListStyles();
            }
        }).FileAndForget("DiffRapid/TableTheme");
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F5 && modifiers == ModifierKeys.None)
        {
            StartCompare();
            e.Handled = true;
        }
        else if (e.Key == Key.F7 && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
        {
            NavigateDifference(modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private void OnOptionBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyEditedOptions();
            e.Handled = true;
        }
    }

    // Compares again when a column or tolerance box holds text the shown comparison was not made with.
    private void ApplyEditedOptions()
    {
        if (_keyColumns.Text.Trim() != _appliedKeys || _ignoreColumns.Text.Trim() != _appliedIgnore || _tolerance.Text.Trim() != _appliedTolerance)
        {
            StartCompare();
        }
    }

    private void StartCompare()
    {
        if (_disposed)
        {
            return;
        }

        if (!TryReadToleranceBox(out var absolute, out var relative))
        {
            _position.Text = $"'{_tolerance.Text.Trim()}' is not a tolerance; use a number such as 0.01, or a percentage such as 1%.";
            return;
        }

        _appliedKeys = _keyColumns.Text.Trim();
        _appliedIgnore = _ignoreColumns.Text.Trim();
        _appliedTolerance = _tolerance.Text.Trim();
        var readOptions = new DelimitedReadOptions
        {
            Delimiter = Delimiters[Math.Max(0, _delimiter.SelectedIndex)].Delimiter,
            HasHeader = _headerRow.IsChecked == true,
        };
        var compareOptions = new TableCompareOptions
        {
            KeyColumns = SplitColumns(_appliedKeys),
            IgnoreColumns = SplitColumns(_appliedIgnore),
            IgnoreCase = _ignoreCase.IsChecked == true,
            TrimWhitespace = _trim.IsChecked == true,
            AbsoluteTolerance = absolute,
            RelativeTolerance = relative,
        };

        _compareCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _compareCancellation = cancellation;
        _comparing = true;
        UpdateCommands();
        _verdict.Text = "Comparing…";
        _verdict.ClearValue(TextBlock.ForegroundProperty);
        _position.Text = _outcome is null ? "Reading the tables…" : "Comparing again…";

        // The tables are read again only when the delimiter or header setting changed.
        var cached = _tables;
        var stopwatch = Stopwatch.StartNew();
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var outcome = await Task.Run(() => Compute(cached, readOptions, compareOptions, cancellation.Token), cancellation.Token);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    Complete(outcome, stopwatch.Elapsed);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    ShowFailure(ex);
                }
            }
        }).FileAndForget("DiffRapid/TableCompare");
    }

    private bool TryReadToleranceBox(out double absolute, out double relative)
    {
        absolute = 0;
        relative = 0;
        var text = _tolerance.Text.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        var isPercent = text.EndsWith("%", StringComparison.Ordinal);
        var number = isPercent ? text.Substring(0, text.Length - 1).Trim() : text;
        var parsed = double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (!parsed || value < 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        if (isPercent)
        {
            relative = value / 100;
        }
        else
        {
            absolute = value;
        }

        return true;
    }

    private static IReadOnlyList<string> SplitColumns(string text) =>
        text.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    // Runs on a background thread: reads the files when needed, compares them and prepares the rows.
    private CompareOutcome Compute(LoadedTables? cached, DelimitedReadOptions readOptions, TableCompareOptions compareOptions, CancellationToken cancellationToken)
    {
        var tables = cached is not null && cached.Options == readOptions
            ? cached
            : new LoadedTables(readOptions, DelimitedReader.ReadFile(_leftPath, readOptions), DelimitedReader.ReadFile(_rightPath, readOptions));
        cancellationToken.ThrowIfCancellationRequested();

        var result = TableComparer.Compare(tables.Left, tables.Right, compareOptions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var all = new List<TableRow>(result.Rows.Count);
        var differences = new List<TableRow>();
        foreach (var row in result.Rows)
        {
            var differenceNumber = row.Status == TableRowStatus.Same ? 0 : differences.Count + 1;
            var tableRow = new TableRow(result, row, differenceNumber);
            all.Add(tableRow);
            if (differenceNumber > 0)
            {
                differences.Add(tableRow);
            }
        }

        return new CompareOutcome(tables, result, all, differences, EstimateColumnWidths(result));
    }

    // Sizes each column from its header and the first rows, so short columns stay narrow.
    private static double[] EstimateColumnWidths(TableDiffResult result)
    {
        var widths = new double[result.Columns.Count];
        for (var c = 0; c < result.Columns.Count; c++)
        {
            var column = result.Columns[c];
            var longest = column.Name.Length + 4;
            longest = Math.Max(longest, LongestCell(result.Left, column.LeftIndex));
            longest = Math.Max(longest, LongestCell(result.Right, column.RightIndex));
            widths[c] = Math.Max(MinCellColumnWidth, Math.Min(MaxCellColumnWidth, (longest * 7.0 * 2) + 24));
        }

        return widths;
    }

    private static int LongestCell(TableData table, int? column)
    {
        if (column is not int index)
        {
            return 0;
        }

        var longest = 0;
        var rows = Math.Min(table.Rows.Count, WidthSampleRows);
        for (var row = 0; row < rows; row++)
        {
            longest = Math.Max(longest, table.Cell(row, index).Length);
        }

        return longest;
    }

    private void Complete(CompareOutcome outcome, TimeSpan elapsed)
    {
        _tables = outcome.Tables;
        _outcome = outcome;
        _comparing = false;
        var result = outcome.Result;
        _leftInfo.Text = DescribeTable(result.Left);
        _rightInfo.Text = DescribeTable(result.Right);
        BuildColumns(outcome);
        ShowRows();
        ShowSummary(result);
        ShowFooterDetails(result, elapsed);
        UpdateCommands();
        UpdatePosition();
    }

    private void ShowFailure(Exception ex)
    {
        _comparing = false;
        _outcome = null;
        _list.ItemsSource = null;
        _view.Columns.Clear();
        _verdict.Text = "Compare failed";
        _verdict.Foreground = DiffTheme.RemovedBrush;
        _position.Text = ex.Message;
        _details.Text = string.Empty;
        _details.ToolTip = null;
        UpdateCommands();
    }

    private static string DescribeTable(TableData table)
    {
        var delimiter = table.Delimiter switch
        {
            ',' => "comma",
            '\t' => "tab",
            ';' => "semicolon",
            '|' => "pipe",
            ' ' => "space",
            var other => "'" + other + "'",
        };
        return $"{Plural(table.Rows.Count, "row")} · {Plural(table.ColumnCount, "column")} · {delimiter}";
    }

    // One status column, one line-number column, then one left|right column per table column. Widths the
    // user dragged are kept for columns of the same name.
    private void BuildColumns(CompareOutcome outcome)
    {
        foreach (var column in _view.Columns)
        {
            if (column.Header is FrameworkElement { Tag: string name })
            {
                _columnWidths[name] = column.ActualWidth;
            }
        }

        _view.Columns.Clear();
        _view.Columns.Add(new GridViewColumn { Header = "Status", Width = 104, CellTemplate = StatusTemplate() });
        _view.Columns.Add(new GridViewColumn { Header = "Line", Width = 92, CellTemplate = LinesTemplate() });
        var columns = outcome.Result.Columns;
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var width = _columnWidths.TryGetValue(column.Name, out var kept) && kept > 0 ? kept : outcome.ColumnWidths[c];
            _view.Columns.Add(new GridViewColumn { Header = ColumnHeader(column), Width = width, CellTemplate = CellTemplate(c) });
        }
    }

    // The column name, a key icon for key columns and a grey note for ignored, one-sided or renamed columns.
    private static FrameworkElement ColumnHeader(TableColumn column)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Tag = column.Name };
        if (column.IsKey)
        {
            panel.Children.Add(new CrispImage { Moniker = KnownMonikers.Key, Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0) });
        }

        panel.Children.Add(new TextBlock { Text = column.Name, FontWeight = column.IsKey ? FontWeights.SemiBold : FontWeights.Normal });
        var note = column.IsIgnored ? "ignored"
            : column.LeftIndex is null ? "right only"
            : column.RightIndex is null ? "left only"
            : column.IsRenamed ? "was " + column.LeftName + ", now " + column.RightName
            : null;
        if (note is not null)
        {
            var text = new TextBlock { Text = "  " + note, FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            panel.Children.Add(text);
        }

        panel.ToolTip = column.IsRenamed ? $"Left: {column.LeftName}{Environment.NewLine}Right: {column.RightName}" : column.Name;
        return panel;
    }

    // A soft pill in the status colour.
    private static DataTemplate StatusTemplate()
    {
        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        pill.SetValue(Border.PaddingProperty, new Thickness(8, 1, 8, 1));
        pill.SetValue(MarginProperty, new Thickness(4, 1, 0, 1));
        pill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(TableRow.StatusTint)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TableRow.StatusText)));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TableRow.StatusBrush)));
        pill.AppendChild(text);
        return new DataTemplate { VisualTree = pill };
    }

    private static DataTemplate LinesTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(MarginProperty, new Thickness(4, 0, 0, 0));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TableRow.Lines)));
        text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return new DataTemplate { VisualTree = text };
    }

    // Left and right halves of one table column, split by a thin line. The row's cells are made on demand, so
    // only rows that scroll into view pay for them.
    private static DataTemplate CellTemplate(int column)
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetBinding(DataContextProperty, new Binding($"{nameof(TableRow.Cells)}[{column}]"));
        var leftColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        leftColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var splitColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        splitColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(1));
        var rightColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
        rightColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        grid.AppendChild(leftColumn);
        grid.AppendChild(splitColumn);
        grid.AppendChild(rightColumn);

        grid.AppendChild(CellHalf(nameof(CellPair.Left), 0));
        var split = new FrameworkElementFactory(typeof(Border));
        split.SetValue(Grid.ColumnProperty, 1);
        split.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        grid.AppendChild(split);
        grid.AppendChild(CellHalf(nameof(CellPair.Right), 2));
        return new DataTemplate { VisualTree = grid };
    }

    private static FrameworkElementFactory CellHalf(string side, int gridColumn)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Grid.ColumnProperty, gridColumn);
        border.SetValue(Border.PaddingProperty, new Thickness(5, 1, 5, 1));
        border.SetBinding(Border.BackgroundProperty, new Binding(side + "." + nameof(CellSide.Background)));
        var text = new FrameworkElementFactory(typeof(CellText));
        text.SetBinding(CellText.SideProperty, new Binding(side));
        border.AppendChild(text);
        return border;
    }

    // Lists every row or only the differences, keeping the selected row when it is still listed.
    private void ShowRows()
    {
        if (_outcome is null)
        {
            return;
        }

        var selected = _list.SelectedItem as TableRow;
        var rows = _showIdentical.IsChecked == true ? _outcome.AllRows : _outcome.Differences;
        _list.ItemsSource = rows;
        if (selected is not null && ReferenceEquals(selected.Result, _outcome.Result) && (selected.DifferenceNumber > 0 || _showIdentical.IsChecked == true))
        {
            _list.SelectedItem = selected;
            _list.ScrollIntoView(selected);
        }

        UpdatePosition();
    }

    // "12 changed, 3 only left, 1 only right; key: Id"
    private void ShowSummary(TableDiffResult result)
    {
        var parts = new List<string>();
        AddCount(parts, result.Count(TableRowStatus.Changed), "changed");
        AddCount(parts, result.Count(TableRowStatus.LeftOnly), "only left");
        AddCount(parts, result.Count(TableRowStatus.RightOnly), "only right");
        AddCount(parts, result.Count(TableRowStatus.Moved), "moved");
        var columnDifferences = result.ColumnDifferences.Count();
        if (columnDifferences > 0)
        {
            parts.Add(Plural(columnDifferences, "column difference"));
        }

        var keys = result.Columns.Where(c => c.IsKey).Select(c => c.Name).ToList();
        var key = "key: " + (keys.Count == 0 ? "row order" : string.Join(", ", keys));
        _verdict.Text = (parts.Count == 0 ? "Identical" : string.Join(", ", parts)) + "; " + key;
        if (parts.Count == 0)
        {
            _verdict.Foreground = DiffTheme.AddedBrush;
        }
        else
        {
            _verdict.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static void AddCount(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add($"{count:N0} {label}");
        }
    }

    // Duplicate keys and column differences on the right of the footer; the full list is in its tooltip.
    private void ShowFooterDetails(TableDiffResult result, TimeSpan elapsed)
    {
        var notes = new List<string>();
        foreach (var duplicate in result.DuplicateKeys)
        {
            var table = duplicate.Side == TableSide.Left ? result.Left : result.Right;
            var side = duplicate.Side == TableSide.Left ? "left" : "right";
            var lines = string.Join(", ", duplicate.Rows.Select(r => table.LineOf(r).ToString(CultureInfo.CurrentCulture)));
            notes.Add($"Duplicate {side} key [{string.Join(", ", duplicate.Key)}] on lines {lines}");
        }

        foreach (var column in result.ColumnDifferences)
        {
            var change = column.LeftIndex is null ? "only on the right" : column.RightIndex is null ? "only on the left" : "renamed to " + column.RightName;
            notes.Add($"Column {column.Name} {change}");
        }

        var elapsedText = elapsed.TotalSeconds < 1 ? $"{elapsed.TotalMilliseconds:0} ms" : $"{elapsed.TotalSeconds:0.0} s";
        var shown = notes.Take(FooterDuplicateLimit).ToList();
        if (notes.Count > shown.Count)
        {
            shown.Add($"{notes.Count - shown.Count:N0} more");
        }

        shown.Add("compared in " + elapsedText);
        _details.Text = string.Join("  ·  ", shown);
        _details.ToolTip = notes.Count == 0 ? null : string.Join(Environment.NewLine, notes);
    }

    // Moves the selection to the next or previous row that differs, wrapping at either end.
    private void NavigateDifference(int direction)
    {
        if (_list.ItemsSource is not List<TableRow> rows || rows.Count == 0)
        {
            return;
        }

        var start = _list.SelectedIndex;
        for (var step = 1; step <= rows.Count; step++)
        {
            var index = start < 0
                ? (direction > 0 ? step - 1 : rows.Count - step)
                : ((start + (direction * step)) % rows.Count + rows.Count) % rows.Count;
            if (rows[index].DifferenceNumber > 0)
            {
                _list.SelectedIndex = index;
                _list.ScrollIntoView(rows[index]);
                if (_list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem item)
                {
                    item.Focus();
                }

                return;
            }
        }
    }

    private void UpdatePosition()
    {
        if (_outcome is null || _comparing)
        {
            return;
        }

        var differences = _outcome.Differences.Count;
        if (_list.SelectedItem is not TableRow row)
        {
            _position.Text = differences == 0 ? "No differences" : $"{Plural(differences, "difference")} · F7 for the next, Shift+F7 for the previous";
            return;
        }

        var where = row.DifferenceNumber > 0 ? $"Difference {row.DifferenceNumber:N0} of {differences:N0}" : "Identical row";
        _position.Text = where + " · " + row.LinesDescription;
    }

    private void UpdateCommands()
    {
        var ready = !_comparing && _outcome is not null;
        var hasDifferences = ready && _outcome!.Differences.Count > 0;
        _previous.IsEnabled = hasDifferences;
        _next.IsEnabled = hasDifferences;
        _exportReport.IsEnabled = ready;
    }

    private void OpenAsText()
    {
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await _package.OpenTextComparisonAsync(_leftPath, _rightPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.LogError(ex);
                _position.Text = ex.Message;
            }
        }).FileAndForget("DiffRapid/TableOpenText");
    }

    // Saves the shown comparison; the save dialog's file type picks HTML or Markdown.
    private void ExportReport()
    {
        if (_comparing || _outcome is null)
        {
            return;
        }

        var leftName = Path.GetFileName(_leftPath);
        var rightName = Path.GetFileName(_rightPath);
        var dialog = new SaveFileDialog
        {
            Title = "Export table comparison report",
            FileName = string.Concat((Path.GetFileNameWithoutExtension(_leftPath) + " vs " + Path.GetFileNameWithoutExtension(_rightPath) + " report")
                .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)),
            Filter = "HTML report (*.html)|*.html|Markdown report (*.md)|*.md",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var html = dialog.FilterIndex == 1;
        var options = new TableReportOptions { Title = leftName + " ↔ " + rightName, LeftLabel = _leftPath, RightLabel = _rightPath };
        try
        {
            using (var writer = new StreamWriter(dialog.FileName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                if (html)
                {
                    TableReportWriter.WriteHtml(_outcome.Result, options, writer);
                }
                else
                {
                    TableReportWriter.WriteMarkdown(_outcome.Result, options, writer);
                }
            }

            _position.Text = $"Saved the report to {dialog.FileName}";
            if (html)
            {
                Process.Start(dialog.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _position.Text = $"Could not save the report: {ex.Message}";
        }
    }

    private static string Plural(int count, string singular) => $"{count:N0} {(count == 1 ? singular : singular + "s")}";

    /// <summary>The two tables as read with one set of read options, kept so option changes skip re-reading.</summary>
    private sealed class LoadedTables
    {
        public LoadedTables(DelimitedReadOptions options, TableData left, TableData right)
        {
            Options = options;
            Left = left;
            Right = right;
        }

        public DelimitedReadOptions Options { get; }

        public TableData Left { get; }

        public TableData Right { get; }
    }

    /// <summary>A finished comparison and the rows prepared for the grid.</summary>
    private sealed class CompareOutcome
    {
        public CompareOutcome(LoadedTables tables, TableDiffResult result, List<TableRow> allRows, List<TableRow> differences, double[] columnWidths)
        {
            Tables = tables;
            Result = result;
            AllRows = allRows;
            Differences = differences;
            ColumnWidths = columnWidths;
        }

        public LoadedTables Tables { get; }

        public TableDiffResult Result { get; }

        public List<TableRow> AllRows { get; }

        public List<TableRow> Differences { get; }

        public double[] ColumnWidths { get; }
    }

    /// <summary>
    /// One row of the grid. Plain data with frozen brushes, so it can be made on the background thread; the
    /// cells are built the first time the grid asks for them.
    /// </summary>
    private sealed class TableRow
    {
        private static readonly Brush ChangedCell = DiffTheme.Tint(DiffTheme.ChangedBrush, 0x30);
        private static readonly Brush ChangedWords = DiffTheme.Tint(DiffTheme.ChangedBrush, 0x70);
        private static readonly Brush LeftOnlyCell = DiffTheme.Tint(DiffTheme.RemovedBrush, 0x30);
        private static readonly Brush RightOnlyCell = DiffTheme.Tint(DiffTheme.AddedBrush, 0x30);
        private static readonly Brush MovedCell = DiffTheme.Tint(DiffTheme.MovedBrush, 0x22);
        private static readonly Brush AbsentCell = DiffTheme.Tint(DiffTheme.IgnoredBrush, 0x18);

        // Shared frozen pill tints: a large table must not make a brush per row.
        private static readonly Dictionary<Brush, Brush> PillTints = new[]
        {
            DiffTheme.ChangedBrush, DiffTheme.RemovedBrush, DiffTheme.AddedBrush, DiffTheme.MovedBrush, DiffTheme.IgnoredBrush,
        }.ToDictionary(accent => accent, accent => DiffTheme.Tint(accent));

        private readonly TableRowDiff _row;
        private CellPair[]? _cells;

        public TableRow(TableDiffResult result, TableRowDiff row, int differenceNumber)
        {
            Result = result;
            _row = row;
            DifferenceNumber = differenceNumber;
            (StatusText, StatusBrush) = row.Status switch
            {
                TableRowStatus.Changed => ("Changed", DiffTheme.ChangedBrush),
                TableRowStatus.LeftOnly => ("Left only", DiffTheme.RemovedBrush),
                TableRowStatus.RightOnly => ("Right only", DiffTheme.AddedBrush),
                TableRowStatus.Moved => (row.HasChangedCells ? "Moved, changed" : "Moved", DiffTheme.MovedBrush),
                _ => ("Identical", DiffTheme.IgnoredBrush),
            };
            StatusTint = PillTints[StatusBrush];
            var leftLine = row.LeftRow is int left ? result.Left.LineOf(left).ToString("N0", CultureInfo.CurrentCulture) : null;
            var rightLine = row.RightRow is int right ? result.Right.LineOf(right).ToString("N0", CultureInfo.CurrentCulture) : null;
            Lines = (leftLine ?? "–") + " ↔ " + (rightLine ?? "–");
            LinesDescription = leftLine is not null && rightLine is not null ? $"left line {leftLine}, right line {rightLine}"
                : leftLine is not null ? $"left line {leftLine}"
                : $"right line {rightLine}";
        }

        public TableDiffResult Result { get; }

        /// <summary>One-based position among the rows that differ, or 0 for an identical row.</summary>
        public int DifferenceNumber { get; }

        public string StatusText { get; }

        public Brush StatusBrush { get; }

        public Brush StatusTint { get; }

        public string Lines { get; }

        public string LinesDescription { get; }

        public IReadOnlyList<CellPair> Cells => _cells ??= BuildCells();

        private CellPair[] BuildCells()
        {
            var columns = Result.Columns;
            var diffs = new TableCellDiff?[columns.Count];
            foreach (var cell in _row.Cells)
            {
                diffs[cell.Column] = cell;
            }

            var cells = new CellPair[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                cells[c] = BuildCell(columns[c], diffs[c]);
            }

            return cells;
        }

        private CellPair BuildCell(TableColumn column, TableCellDiff? diff)
        {
            var opacity = column.IsIgnored ? 0.5 : 1.0;
            if (diff is { IsChanged: true })
            {
                return new CellPair(
                    new CellSide(diff.Left, diff.Inline?.Left, ChangedCell, ChangedWords, opacity),
                    new CellSide(diff.Right, diff.Inline?.Right, ChangedCell, ChangedWords, opacity));
            }

            var background = _row.Status switch
            {
                TableRowStatus.LeftOnly => LeftOnlyCell,
                TableRowStatus.RightOnly => RightOnlyCell,
                TableRowStatus.Moved => MovedCell,
                _ => null,
            };
            var leftText = diff?.Left ?? Text(Result.Left, _row.LeftRow, column.LeftIndex);
            var rightText = diff?.Right ?? Text(Result.Right, _row.RightRow, column.RightIndex);
            return new CellPair(
                new CellSide(leftText ?? string.Empty, null, leftText is null ? AbsentCell : background, null, opacity),
                new CellSide(rightText ?? string.Empty, null, rightText is null ? AbsentCell : background, null, opacity));
        }

        private static string? Text(TableData table, int? row, int? column) =>
            row is int r && column is int c ? table.Cell(r, c) : null;
    }

    /// <summary>The left and right halves of one cell of the grid.</summary>
    private sealed class CellPair
    {
        public CellPair(CellSide left, CellSide right)
        {
            Left = left;
            Right = right;
        }

        public CellSide Left { get; }

        public CellSide Right { get; }
    }

    /// <summary>One side of a cell: its text, the changed word ranges and how to paint them.</summary>
    private sealed class CellSide
    {
        public CellSide(string text, IReadOnlyList<TextSpan>? changes, Brush? background, Brush? highlight, double opacity)
        {
            Text = text;
            Changes = changes;
            Background = background;
            Highlight = highlight;
            Opacity = opacity;
        }

        public string Text { get; }

        public IReadOnlyList<TextSpan>? Changes { get; }

        public Brush? Background { get; }

        public Brush? Highlight { get; }

        public double Opacity { get; }
    }

    /// <summary>
    /// A one-line text block that shows a cell side with its changed words highlighted. Line breaks inside a
    /// quoted cell are shown as a symbol so every row keeps one line (the character offsets stay the same).
    /// </summary>
    private sealed class CellText : TextBlock
    {
        public static readonly DependencyProperty SideProperty = DependencyProperty.Register(
            nameof(Side), typeof(CellSide), typeof(CellText), new PropertyMetadata(null, (d, _) => ((CellText)d).Render()));

        public CellText()
        {
            TextTrimming = TextTrimming.CharacterEllipsis;
            VerticalAlignment = VerticalAlignment.Center;
        }

        public CellSide? Side
        {
            get => (CellSide?)GetValue(SideProperty);
            set => SetValue(SideProperty, value);
        }

        private void Render()
        {
            Inlines.Clear();
            var side = Side;
            if (side is null)
            {
                ToolTip = null;
                return;
            }

            Opacity = side.Opacity;
            var text = OneLine(side.Text);
            ToolTip = text.Length == 0 ? null : side.Text;
            if (side.Changes is not { Count: > 0 } changes || side.Highlight is null)
            {
                Inlines.Add(new Run(text));
                return;
            }

            var at = 0;
            foreach (var change in changes)
            {
                var start = Math.Max(at, Math.Min(change.Start, text.Length));
                var end = Math.Min(change.End, text.Length);
                if (start > at)
                {
                    Inlines.Add(new Run(text.Substring(at, start - at)));
                }

                if (end > start)
                {
                    Inlines.Add(new Run(text.Substring(start, end - start)) { Background = side.Highlight });
                    at = end;
                }
                else
                {
                    at = start;
                }
            }

            if (at < text.Length)
            {
                Inlines.Add(new Run(text.Substring(at)));
            }
        }

        private static string OneLine(string text) =>
            text.IndexOfAny(new[] { '\r', '\n' }) < 0 ? text : text.Replace('\r', ' ').Replace('\n', '↵');
    }
}
