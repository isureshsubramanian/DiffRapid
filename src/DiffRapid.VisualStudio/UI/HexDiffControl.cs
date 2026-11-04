using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Core.Binary;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Side-by-side hex comparison of two binary files. The compare runs in the background; the rows are then
/// kept as a compact run-length map of the engine's layout, and only the rows on screen are read from disk
/// and drawn, so files of hundreds of megabytes scroll as smoothly as small ones.
/// </summary>
internal sealed class HexDiffControl : DockPanel, IComparisonView, IOleCommandTarget
{
    private const int WheelRows = 3;

    private static readonly int[] BytesPerLineChoices = { 8, 16, 32 };

    private readonly DiffRapidPackage _package;
    private readonly string _leftPath;
    private readonly string _rightPath;
    private readonly HexPane _leftPane;
    private readonly HexPane _rightPane;
    private readonly HexOverview _overview;
    private readonly ScrollBar _verticalBar = new() { Orientation = Orientation.Vertical, SmallChange = 1 };
    private readonly ScrollBar _horizontalBar = new() { Orientation = Orientation.Horizontal, SmallChange = 20 };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly ComboBox _bytesPerLineBox = new() { MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How many bytes each row shows" };
    private readonly ToggleButton _alignToggle;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

    // Formatted rows of the current screen, so the two panes and repaints share one read per row.
    private readonly Dictionary<long, HexLinePair?> _rowCache = new();

    private BinaryDiffResult? _result;
    private RowMap? _rows;
    private HexFormatter _formatter = new();
    private FileStream? _leftStream;
    private FileStream? _rightStream;
    private CancellationTokenSource? _compareCancellation;
    private int _bytesPerLine = 16;
    private bool _alignInsertions = true;
    private long _topRow;
    private double _horizontalOffset;
    private long _selectedRow = -1;
    private int _selectedByte;
    private int _currentDifference = -1;
    private string? _readError;
    private bool _updatingScrollBars;
    private bool _disposed;

    public HexDiffControl(DiffRapidPackage package, string leftPath, string rightPath)
    {
        _package = package;
        _leftPath = Path.GetFullPath(leftPath);
        _rightPath = Path.GetFullPath(rightPath);

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        _previous = DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous difference (Shift+F7 or Shift+F8)", () => NavigateDifference(-1));
        _next = DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next difference (F7 or F8)", () => NavigateDifference(+1));
        _alignToggle = DiffTheme.OptionToggle(
            "Align insertions",
            "Detect inserted and deleted bytes so the rest of the files line up again (files up to 64 MB); off compares byte by byte at equal offsets",
            v =>
            {
                _alignInsertions = v;
                StartCompare();
            },
            _alignInsertions);

        _leftPane = new HexPane(this, BinarySide.Left);
        _rightPane = new HexPane(this, BinarySide.Right);
        _overview = new HexOverview(this);

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var header = BuildHeader();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildBody());

        PreviewKeyDown += OnPreviewKeyDown;
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
        _compareCancellation?.Cancel();
        CloseStreams();
    }

    /// <summary>The rows on screen; null until the first comparison finishes, then kept while the next one runs.</summary>
    private RowMap? Rows => _rows;

    private FrameworkElement BuildToolbar()
    {
        var bar = new Grid { Margin = new Thickness(6, 4, 6, 4) };

        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Compare again (F5)", StartCompare));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_previous);
        summary.Children.Add(_next);
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_verdict);
        bar.Children.Add(summary);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(_alignToggle);
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.Caption("BYTES PER LINE"));
        _bytesPerLineBox.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var choice in BytesPerLineChoices)
        {
            _bytesPerLineBox.Items.Add(choice);
        }

        _bytesPerLineBox.SelectedItem = _bytesPerLine;
        _bytesPerLineBox.SelectionChanged += (_, _) =>
        {
            if (_bytesPerLineBox.SelectedItem is int bytesPerLine && bytesPerLine != _bytesPerLine)
            {
                _bytesPerLine = bytesPerLine;
                StartLayout();
            }
        };
        settings.Children.Add(_bytesPerLineBox);
        bar.Children.Add(settings);
        DiffTheme.WrapWhenNarrow(bar, summary, settings);

        var chrome = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        chrome.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.CommandBarGradientBeginColorKey);
        return chrome;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = FileLabel("LEFT", _leftPath, DiffTheme.RemovedBrush);
        var right = FileLabel("RIGHT", _rightPath, DiffTheme.AddedBrush);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var chrome = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 7, 10, 7) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    private static FrameworkElement FileLabel(string side, string path, Brush accent)
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
        var text = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel { ToolTip = path, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(text);
        return row;
    }

    private FrameworkElement BuildFooter()
    {
        var panel = new DockPanel { Margin = new Thickness(10, 3, 10, 3) };
        SetDock(_position, Dock.Left);
        panel.Children.Add(_position);
        SetDock(_details, Dock.Right);
        panel.Children.Add(_details);
        _note.Foreground = DiffTheme.ChangedBrush;
        panel.Children.Add(_note);

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return chrome;
    }

    // Two panes side by side with a thin divider, one vertical and one horizontal scroll bar shared by both,
    // and the overview strip of all differences on the right.
    private UIElement BuildBody()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var divider = new Border { Width = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_rightPane, 2);
        Grid.SetColumn(_verticalBar, 3);
        Grid.SetColumn(_overview, 4);
        Grid.SetRow(_horizontalBar, 1);
        Grid.SetColumnSpan(_horizontalBar, 3);
        grid.Children.Add(_leftPane);
        grid.Children.Add(divider);
        grid.Children.Add(_rightPane);
        grid.Children.Add(_verticalBar);
        grid.Children.Add(_overview);
        grid.Children.Add(_horizontalBar);

        _verticalBar.ValueChanged += (_, e) =>
        {
            if (!_updatingScrollBars)
            {
                ScrollTo((long)Math.Round(e.NewValue));
            }
        };
        _horizontalBar.ValueChanged += (_, e) =>
        {
            if (!_updatingScrollBars)
            {
                _horizontalOffset = e.NewValue;
                InvalidatePanes();
            }
        };
        _leftPane.SizeChanged += (_, _) => UpdateScrollBars();
        _rightPane.SizeChanged += (_, _) => UpdateScrollBars();
        return grid;
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>Compares the files again in the current mode, then lays out the rows.</summary>
    private void StartCompare() => Start(recompare: true);

    /// <summary>Lays out the rows again for a new line width, keeping the comparison.</summary>
    private void StartLayout() => Start(recompare: _result is null);

    private void Start(bool recompare)
    {
        if (_disposed)
        {
            return;
        }

        _compareCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _compareCancellation = cancellation;

        // Keep the user's place across a new line width or a recompare: the left offset at the top and selected.
        var topOffset = OffsetAtRow(_topRow, BinarySide.Left);
        var selectedOffset = _selectedRow >= 0 ? OffsetAtRow(_selectedRow, BinarySide.Left) + _selectedByte : (long?)null;

        var mode = _alignInsertions ? BinaryCompareMode.Resync : BinaryCompareMode.Aligned;
        var bytesPerLine = _bytesPerLine;
        var previous = recompare ? null : _result;
        _verdict.Text = recompare ? "Comparing…" : "Laying out rows…";
        _verdict.ClearValue(TextBlock.ForegroundProperty);
        SetNavigationEnabled(false);

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var (result, rows) = await Task.Run(
                    () =>
                    {
                        var compared = previous ?? BinaryComparer.CompareFiles(_leftPath, _rightPath, new BinaryCompareOptions { Mode = mode }, cancellation.Token);
                        return (compared, RowMap.Build(compared, bytesPerLine, cancellation.Token));
                    },
                    cancellation.Token);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    Complete(result, rows, bytesPerLine, topOffset, selectedOffset);
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
                    _verdict.Text = "Compare failed";
                    _verdict.Foreground = DiffTheme.RemovedBrush;
                    _position.Text = ex.Message;
                }
            }
        }).FileAndForget("DiffRapid/HexCompare");
    }

    private void Complete(BinaryDiffResult result, RowMap rows, int bytesPerLine, long? topOffset, long? selectedOffset)
    {
        if (!ReferenceEquals(result, _result))
        {
            // A new comparison: reopen the files, so the bytes shown are the ones that were compared.
            CloseStreams();
            _leftStream = OpenShared(_leftPath);
            _rightStream = OpenShared(_rightPath);
        }

        _result = result;
        _rows = rows;
        _readError = null;
        _rowCache.Clear();

        // Wide enough offsets for the larger file, so every row's offset column has the same width.
        var options = new HexFormatOptions { BytesPerLine = bytesPerLine };
        var digits = Math.Max(options.OffsetDigits, new HexFormatter(options).FormatOffset(Math.Max(result.LeftLength, result.RightLength)).Length);
        _formatter = new HexFormatter(options with { OffsetDigits = digits });

        _topRow = topOffset is { } top ? rows.FindRow(BinarySide.Left, top) : 0;
        _selectedRow = -1;
        _selectedByte = 0;
        if (selectedOffset is { } selected && rows.RowCount > 0)
        {
            _selectedRow = rows.FindRow(BinarySide.Left, selected);
            _selectedByte = (int)Math.Max(0, Math.Min(bytesPerLine - 1, selected - rows[_selectedRow].LeftOffset));
        }

        _currentDifference = -1;
        UpdateSummary();
        SetNavigationEnabled(!result.AreEqual);
        UpdateScrollBars();
        _overview.InvalidateVisual();
        InvalidatePanes();
        UpdatePosition();
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    private void CloseStreams()
    {
        _leftStream?.Dispose();
        _rightStream?.Dispose();
        _leftStream = null;
        _rightStream = null;
    }

    private void UpdateSummary()
    {
        var result = _result;
        if (result is null)
        {
            return;
        }

        if (result.AreEqual)
        {
            _verdict.Text = "Identical";
            _verdict.Foreground = DiffTheme.AddedBrush;
        }
        else
        {
            var bytes = result.LeftDifferentBytes == result.RightDifferentBytes
                ? Plural(result.LeftDifferentBytes, "byte") + " differ"
                : $"{result.LeftDifferentBytes:N0} left and {result.RightDifferentBytes:N0} right bytes differ";
            _verdict.Text = Plural(result.DifferenceCount, "difference") + ", " + bytes;
            _verdict.Foreground = DiffTheme.ChangedBrush;
        }

        _details.Text = $"Left {FormatSize(result.LeftLength)}  ·  Right {FormatSize(result.RightLength)}";
        _details.ToolTip = $"Left: {result.LeftLength:N0} bytes\nRight: {result.RightLength:N0} bytes\nMode: " + (result.Mode == BinaryCompareMode.Resync ? "insertions aligned" : "byte by byte");
        _note.Text = result.Note ?? string.Empty;
        _note.ToolTip = result.Note;
    }

    private static string Plural(long count, string noun) => count.ToString("N0", CultureInfo.CurrentCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    private static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? Plural(bytes, "byte") : $"{size:0.#} {units[unit]} ({bytes:N0} bytes)";
    }

    private void SetNavigationEnabled(bool enabled)
    {
        _previous.IsEnabled = enabled;
        _next.IsEnabled = enabled;
    }

    // F7 / F8 step through the differences wherever focus is in the view; F5 compares again.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.F7 or Key.F8 when modifiers == ModifierKeys.None:
                NavigateDifference(+1);
                e.Handled = true;
                break;
            case Key.F7 or Key.F8 when modifiers == ModifierKeys.Shift:
                NavigateDifference(-1);
                e.Handled = true;
                break;
            case Key.F5 when modifiers == ModifierKeys.None:
                StartCompare();
                e.Handled = true;
                break;
        }
    }

    // The keys that move the selection, when a pane has focus.
    private void OnPaneKeyDown(KeyEventArgs e)
    {
        var rows = Rows;
        if (rows is null || rows.RowCount == 0)
        {
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var row = Math.Max(_selectedRow, _topRow);
        var bytesPerLine = BytesPerLineShown;
        var column = _selectedRow >= 0 ? _selectedByte : 0;
        var page = Math.Max(1, VisibleRows - 1);
        switch (e.Key)
        {
            case Key.Up:
                row--;
                break;
            case Key.Down:
                row++;
                break;
            case Key.Left when column > 0:
                column--;
                break;
            case Key.Left:
                row--;
                column = bytesPerLine - 1;
                break;
            case Key.Right when column < bytesPerLine - 1:
                column++;
                break;
            case Key.Right:
                row++;
                column = 0;
                break;
            case Key.PageUp:
                row -= page;
                break;
            case Key.PageDown:
                row += page;
                break;
            case Key.Home when control:
                row = 0;
                column = 0;
                break;
            case Key.Home:
                column = 0;
                break;
            case Key.End when control:
                row = rows.RowCount - 1;
                column = bytesPerLine - 1;
                break;
            case Key.End:
                column = bytesPerLine - 1;
                break;
            default:
                return;
        }

        e.Handled = true;
        Select(Math.Max(0, Math.Min(rows.RowCount - 1, row)), column);
    }

    /// <summary>Selects a byte (a column of a row) and scrolls it into view.</summary>
    private void Select(long row, int column)
    {
        _selectedRow = row;
        _selectedByte = Math.Max(0, Math.Min(BytesPerLineShown - 1, column));
        EnsureVisible(row, centre: false);
        InvalidatePanes();
        UpdatePosition();
    }

    /// <summary>Steps to the next or previous difference relative to the selection (or the top of the view).</summary>
    private void NavigateDifference(int direction)
    {
        var result = _result;
        var rows = Rows;
        if (result is null || rows is null || result.Ranges.Count == 0)
        {
            return;
        }

        var from = _selectedRow >= 0 ? (_selectedRow, _selectedByte) : direction > 0 ? (_topRow - 1, int.MaxValue) : (_topRow, 0);
        int index;
        if (direction > 0)
        {
            index = FirstRangeAfter(result, rows, from);
            if (index >= result.Ranges.Count)
            {
                return;
            }
        }
        else
        {
            index = FirstRangeAtOrAfter(result, rows, from) - 1;
            if (index < 0)
            {
                return;
            }
        }

        _currentDifference = index;
        var (row, column) = RangeStart(result, rows, index);
        _selectedRow = row;
        _selectedByte = column;
        EnsureVisible(row, centre: true);
        InvalidatePanes();
        UpdatePosition();
        if (!_leftPane.IsKeyboardFocusWithin && !_rightPane.IsKeyboardFocusWithin)
        {
            _leftPane.Focus();
        }
    }

    /// <summary>The index of the first range that starts after a position (ranges are in row order).</summary>
    private static int FirstRangeAfter(BinaryDiffResult result, RowMap rows, (long Row, int Column) position) =>
        BinarySearchRanges(result, rows, start => Compare(start, position) > 0);

    /// <summary>The index of the first range that starts at or after a position.</summary>
    private static int FirstRangeAtOrAfter(BinaryDiffResult result, RowMap rows, (long Row, int Column) position) =>
        BinarySearchRanges(result, rows, start => Compare(start, position) >= 0);

    private static int BinarySearchRanges(BinaryDiffResult result, RowMap rows, Func<(long Row, int Column), bool> isAtOrPast)
    {
        var low = 0;
        var high = result.Ranges.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (isAtOrPast(RangeStart(result, rows, middle)))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private static int Compare((long Row, int Column) a, (long Row, int Column) b) =>
        a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column);

    /// <summary>The row and column where a range's first byte is shown; an insertion is found on the right.</summary>
    private static (long Row, int Column) RangeStart(BinaryDiffResult result, RowMap rows, int index)
    {
        var range = result.Ranges[index];
        var side = range.LeftLength > 0 ? BinarySide.Left : BinarySide.Right;
        var offset = range.OffsetOn(side);
        var row = rows.FindRow(side, offset);
        var layout = rows[row];
        var rowOffset = side == BinarySide.Left ? layout.LeftOffset : layout.RightOffset;
        return (row, (int)Math.Max(0, offset - rowOffset));
    }

    private void UpdatePosition()
    {
        var rows = Rows;
        var result = _result;
        if (rows is null || result is null)
        {
            _position.Text = string.Empty;
            return;
        }

        if (_readError is not null)
        {
            _position.Text = _readError;
            return;
        }

        var parts = new List<string>();
        if (_currentDifference >= 0)
        {
            parts.Add($"Difference {_currentDifference + 1:N0} of {result.DifferenceCount:N0}");
        }

        if (_selectedRow >= 0 && _selectedRow < rows.RowCount)
        {
            var layout = rows[_selectedRow];
            parts.Add("Left " + DescribeOffset(layout.LeftOffset, layout.LeftCount));
            parts.Add("Right " + DescribeOffset(layout.RightOffset, layout.RightCount));
        }
        else if (rows.RowCount > 0)
        {
            parts.Add("Click a byte to see its offset");
        }

        _position.Text = string.Join("  ·  ", parts);
    }

    private string DescribeOffset(long rowOffset, int count)
    {
        if (_selectedByte >= count)
        {
            return "—";
        }

        var offset = rowOffset + _selectedByte;
        return $"0x{_formatter.FormatOffset(offset)} ({offset:N0})";
    }

    /// <summary>The offset on one side of the first byte of a row, or null when there are no rows.</summary>
    private long? OffsetAtRow(long row, BinarySide side)
    {
        var rows = Rows;
        if (rows is null || rows.RowCount == 0)
        {
            return null;
        }

        var layout = rows[Math.Max(0, Math.Min(rows.RowCount - 1, row))];
        return side == BinarySide.Left ? layout.LeftOffset : layout.RightOffset;
    }

    /// <summary>The line width of the rows on screen; a new combo box choice applies once its layout is ready.</summary>
    private int BytesPerLineShown => _formatter.Options.BytesPerLine;

    /// <summary>The number of whole rows that fit in a pane.</summary>
    private int VisibleRows => Math.Max(1, _leftPane.VisibleRows);

    private long MaxTopRow => Math.Max(0, (Rows?.RowCount ?? 0) - VisibleRows);

    private void ScrollTo(long row)
    {
        var top = Math.Max(0, Math.Min(MaxTopRow, row));
        if (top == _topRow)
        {
            return;
        }

        _topRow = top;
        UpdateScrollBars();
        InvalidatePanes();
    }

    private void ScrollBy(long rows) => ScrollTo(_topRow + rows);

    private void EnsureVisible(long row, bool centre)
    {
        if (row >= _topRow && row < _topRow + VisibleRows)
        {
            return;
        }

        if (centre)
        {
            ScrollTo(row - (VisibleRows / 3));
        }
        else
        {
            ScrollTo(row < _topRow ? row : row - VisibleRows + 1);
        }
    }

    private void UpdateScrollBars()
    {
        _updatingScrollBars = true;
        try
        {
            _topRow = Math.Max(0, Math.Min(MaxTopRow, _topRow));
            var visible = VisibleRows;
            _verticalBar.Minimum = 0;
            _verticalBar.Maximum = MaxTopRow;
            _verticalBar.ViewportSize = visible;
            _verticalBar.LargeChange = Math.Max(1, visible - 1);
            _verticalBar.Value = _topRow;

            var paneWidth = Math.Min(_leftPane.ActualWidth, _rightPane.ActualWidth);
            var overflow = Math.Max(0, _leftPane.ContentWidth - paneWidth);
            _horizontalOffset = Math.Min(_horizontalOffset, overflow);
            _horizontalBar.Maximum = overflow;
            _horizontalBar.ViewportSize = Math.Max(1, paneWidth);
            _horizontalBar.LargeChange = Math.Max(1, paneWidth / 2);
            _horizontalBar.Value = _horizontalOffset;
            _horizontalBar.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingScrollBars = false;
        }

        _overview.InvalidateVisual();
    }

    private void InvalidatePanes()
    {
        // Rows that scrolled away are dropped; the panes refill the cache for the rows now on screen.
        if (_rowCache.Count > 4 * (VisibleRows + 2))
        {
            _rowCache.Clear();
        }

        _leftPane.InvalidateVisual();
        _rightPane.InvalidateVisual();
    }

    /// <summary>Reads and formats one row, once per screen; null when it cannot be read.</summary>
    private HexLinePair? FormatRow(long row)
    {
        if (_rowCache.TryGetValue(row, out var cached))
        {
            return cached;
        }

        HexLinePair? pair = null;
        if (Rows is { } rows && _result is { } result && _leftStream is { } left && _rightStream is { } right && row < rows.RowCount)
        {
            try
            {
                pair = HexPairedView.FormatRow(left, right, rows[row], result, _formatter);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The file changed or went away since it was compared; say so instead of showing stale bytes.
                if (_readError is null)
                {
                    _readError = ex.Message + " Compare again (F5) to refresh.";
                    _position.Text = _readError;
                }
            }
        }

        _rowCache[row] = pair;
        return pair;
    }

    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (cCmds == 1 && OwnCommand(pguidCmdGroup, prgCmds[0].cmdID) is not null)
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (OwnCommand(pguidCmdGroup, nCmdID) is { } command)
        {
            command();
            return VSConstants.S_OK;
        }

        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    // The DiffRapid previous / next difference commands, for anyone who binds keys to them globally.
    private Action? OwnCommand(Guid group, uint id)
    {
        if (group == PackageIds.CommandSet)
        {
            return (int)id switch
            {
                PackageIds.PreviousDifference => () => NavigateDifference(-1),
                PackageIds.NextDifference => () => NavigateDifference(+1),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// The row layout as runs of evenly spaced rows. Rows of a run have the same byte counts on both sides and
    /// the same difference flag, so a file of millions of identical rows needs only a few runs, and any row
    /// is found by a binary search.
    /// </summary>
    private sealed class RowMap
    {
        private readonly List<Run> _runs;

        private RowMap(List<Run> runs, long rowCount)
        {
            _runs = runs;
            RowCount = rowCount;
        }

        public long RowCount { get; }

        public IReadOnlyList<Run> Runs => _runs;

        public HexRowLayout this[long row]
        {
            get
            {
                var run = _runs[RunIndexOfRow(row)];
                var step = row - run.FirstRow;
                return new HexRowLayout(
                    run.LeftOffset + (step * run.LeftCount),
                    run.LeftCount,
                    run.RightOffset + (step * run.RightCount),
                    run.RightCount,
                    run.HasDifference);
            }
        }

        public static RowMap Build(BinaryDiffResult result, int bytesPerLine, CancellationToken cancellationToken)
        {
            var runs = new List<Run>();
            Run? current = null;
            long count = 0;
            foreach (var row in HexPairedView.Layout(result, bytesPerLine))
            {
                if ((count & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (current is null || !current.Continues(row))
                {
                    current = new Run(count, row);
                    runs.Add(current);
                }
                else
                {
                    current.RowCount++;
                }

                count++;
            }

            return new RowMap(runs, count);
        }

        /// <summary>The row showing a byte of one file; the nearest row when the offset is past the end.</summary>
        public long FindRow(BinarySide side, long offset)
        {
            if (_runs.Count == 0)
            {
                return 0;
            }

            // The last run starting at or before the offset; runs are ordered by offset on both sides.
            var low = 0;
            var high = _runs.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (_runs[middle].OffsetOn(side) <= offset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            var index = Math.Max(0, low - 1);

            // Filler runs show no bytes of this side; the byte is in a run before it.
            while (index > 0 && _runs[index].CountOn(side) == 0)
            {
                index--;
            }

            var run = _runs[index];
            var perRow = run.CountOn(side);
            var step = perRow == 0 ? 0 : (offset - run.OffsetOn(side)) / perRow;
            return run.FirstRow + Math.Max(0, Math.Min(run.RowCount - 1, step));
        }

        private int RunIndexOfRow(long row)
        {
            var low = 0;
            var high = _runs.Count - 1;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                if (_runs[middle].FirstRow <= row)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }
    }

    private sealed class Run
    {
        public Run(long firstRow, HexRowLayout row)
        {
            FirstRow = firstRow;
            RowCount = 1;
            LeftOffset = row.LeftOffset;
            LeftCount = row.LeftCount;
            RightOffset = row.RightOffset;
            RightCount = row.RightCount;
            HasDifference = row.HasDifference;
        }

        public long FirstRow { get; }

        public long RowCount { get; set; }

        public long LeftOffset { get; }

        public int LeftCount { get; }

        public long RightOffset { get; }

        public int RightCount { get; }

        public bool HasDifference { get; }

        public long OffsetOn(BinarySide side) => side == BinarySide.Left ? LeftOffset : RightOffset;

        public int CountOn(BinarySide side) => side == BinarySide.Left ? LeftCount : RightCount;

        /// <summary>Whether a row is the next one of this run.</summary>
        public bool Continues(HexRowLayout row) =>
            row.HasDifference == HasDifference
            && row.LeftCount == LeftCount
            && row.RightCount == RightCount
            && row.LeftOffset == LeftOffset + (RowCount * LeftCount)
            && row.RightOffset == RightOffset + (RowCount * RightCount);
    }

    /// <summary>
    /// One side of the view: a column ruler, then offset | hex | ASCII for the rows on screen. It draws only
    /// the visible rows, asking the control to read them, so its cost does not depend on the file size.
    /// </summary>
    private sealed class HexPane : FrameworkElement
    {
        public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
            nameof(Background), typeof(Brush), typeof(HexPane), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            nameof(Foreground), typeof(Brush), typeof(HexPane), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GrayTextProperty = DependencyProperty.Register(
            nameof(GrayText), typeof(Brush), typeof(HexPane), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RuleProperty = DependencyProperty.Register(
            nameof(Rule), typeof(Brush), typeof(HexPane), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
            nameof(Selection), typeof(Brush), typeof(HexPane), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double FontSize = 13;
        private const double Padding = 8;

        private static readonly Typeface Mono = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Brush ChangedByte = DiffTheme.Tint(DiffTheme.ChangedBrush, 0x66);
        private static readonly Brush ChangedRow = DiffTheme.Tint(DiffTheme.ChangedBrush, 0x18);
        private static readonly Brush InsertedByte = DiffTheme.Tint(DiffTheme.AddedBrush, 0x55);
        private static readonly Brush InsertedRow = DiffTheme.Tint(DiffTheme.AddedBrush, 0x18);
        private static readonly Brush DeletedByte = DiffTheme.Tint(DiffTheme.RemovedBrush, 0x55);
        private static readonly Brush DeletedRow = DiffTheme.Tint(DiffTheme.RemovedBrush, 0x18);
        private static readonly Brush Filler = DiffTheme.Tint(DiffTheme.IgnoredBrush, 0x28);

        private readonly HexDiffControl _owner;
        private readonly BinarySide _side;
        private double _charWidth;
        private double _rowHeight;
        private double _pixelsPerDip = 1.0;

        public HexPane(HexDiffControl owner, BinarySide side)
        {
            _owner = owner;
            _side = side;
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Cursor = Cursors.IBeam;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            SetResourceReference(GrayTextProperty, EnvironmentColors.SystemGrayTextBrushKey);
            SetResourceReference(RuleProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            SetResourceReference(SelectionProperty, EnvironmentColors.SystemHighlightBrushKey);
            MeasureFont(1.0);
            Loaded += (_, _) =>
            {
                MeasureFont(VisualTreeHelper.GetDpi(this).PixelsPerDip);
                _owner.UpdateScrollBars();
            };
        }

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public Brush GrayText
        {
            get => (Brush)GetValue(GrayTextProperty);
            set => SetValue(GrayTextProperty, value);
        }

        public Brush Rule
        {
            get => (Brush)GetValue(RuleProperty);
            set => SetValue(RuleProperty, value);
        }

        public Brush Selection
        {
            get => (Brush)GetValue(SelectionProperty);
            set => SetValue(SelectionProperty, value);
        }

        /// <summary>The number of whole rows below the ruler.</summary>
        public int VisibleRows => (int)Math.Max(1, Math.Floor((ActualHeight - HeaderHeight) / _rowHeight));

        /// <summary>The width of a whole row, for the horizontal scroll bar.</summary>
        public double ContentWidth => (2 * Padding) + ((Columns(_owner._formatter).Ascii + _owner.BytesPerLineShown) * _charWidth);

        private double HeaderHeight => _rowHeight + 4;

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            MeasureFont(newDpi.PixelsPerDip);
            base.OnDpiChanged(oldDpi, newDpi);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = new Rect(RenderSize);
            dc.DrawRectangle(Background, null, size);
            var formatter = _owner._formatter;
            var rows = _owner.Rows;
            var (hexStart, asciiStart) = Columns(formatter);

            dc.PushClip(new RectangleGeometry(size));
            dc.PushTransform(new TranslateTransform(Padding - _owner._horizontalOffset, 0));

            // The ruler: the byte number of each hex column.
            DrawText(dc, "Offset", 0, 2, GrayText);
            var ruler = new byte[formatter.Options.BytesPerLine];
            for (var i = 0; i < ruler.Length; i++)
            {
                ruler[i] = (byte)i;
            }

            DrawText(dc, formatter.FormatHex(ruler), hexStart, 2, GrayText);
            dc.Pop();
            dc.DrawRectangle(Rule, null, new Rect(0, HeaderHeight - 1, ActualWidth, 1));
            dc.PushTransform(new TranslateTransform(Padding - _owner._horizontalOffset, 0));

            if (rows is not null)
            {
                var visible = VisibleRows + 1;
                for (var i = 0; i < visible; i++)
                {
                    var row = _owner._topRow + i;
                    if (row >= rows.RowCount)
                    {
                        break;
                    }

                    DrawRow(dc, rows, row, HeaderHeight + (i * _rowHeight), hexStart, asciiStart);
                }
            }

            dc.Pop();
            dc.Pop();
        }

        private void DrawRow(DrawingContext dc, RowMap rows, long row, double top, int hexStart, int asciiStart)
        {
            var layout = rows[row];
            var count = _side == BinarySide.Left ? layout.LeftCount : layout.RightCount;
            var otherCount = _side == BinarySide.Left ? layout.RightCount : layout.LeftCount;
            var rowWidth = (asciiStart + _owner.BytesPerLineShown) * _charWidth;
            var rowRect = new Rect(-Padding, top, rowWidth + (2 * Padding), _rowHeight);

            // Filler where the other side has bytes this side lacks; otherwise a soft tint on differing rows.
            if (count == 0)
            {
                dc.DrawRectangle(Filler, null, rowRect);
                return;
            }

            var (byteBrush, rowBrush) = otherCount == 0
                ? (_side == BinarySide.Left ? (DeletedByte, DeletedRow) : (InsertedByte, InsertedRow))
                : (ChangedByte, ChangedRow);
            if (layout.HasDifference)
            {
                dc.DrawRectangle(rowBrush, null, rowRect);
            }

            var line = _owner.FormatRow(row);
            var hexLine = _side == BinarySide.Left ? line?.Left : line?.Right;
            if (hexLine is null)
            {
                return;
            }

            var formatter = _owner._formatter;
            for (var i = 0; i < hexLine.Different.Count; i++)
            {
                if (hexLine.Different[i])
                {
                    dc.DrawRectangle(byteBrush, null, new Rect((hexStart + formatter.HexColumn(i)) * _charWidth, top, 2 * _charWidth, _rowHeight));
                    dc.DrawRectangle(byteBrush, null, new Rect((asciiStart + i) * _charWidth, top, _charWidth, _rowHeight));
                }
            }

            if (row == _owner._selectedRow && _owner._selectedByte < hexLine.Bytes.Count)
            {
                var pen = new Pen(Selection, IsKeyboardFocusWithin ? 1.5 : 1);
                var column = _owner._selectedByte;
                dc.DrawRectangle(null, pen, new Rect(((hexStart + formatter.HexColumn(column)) * _charWidth) - 1, top, (2 * _charWidth) + 2, _rowHeight));
                dc.DrawRectangle(null, pen, new Rect((asciiStart + column) * _charWidth, top, _charWidth, _rowHeight));
            }

            var textTop = top + 1;
            DrawText(dc, hexLine.OffsetText, 0, textTop, GrayText);
            DrawText(dc, hexLine.Hex, hexStart, textTop, Foreground);
            DrawText(dc, hexLine.Ascii, asciiStart, textTop, Foreground);
        }

        private void DrawText(DrawingContext dc, string text, int column, double top, Brush brush)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, brush, _pixelsPerDip);
            dc.DrawText(formatted, new Point(column * _charWidth, top));
        }

        /// <summary>The character columns where the hex and ASCII parts start: two spaces after the one before.</summary>
        private static (int Hex, int Ascii) Columns(HexFormatter formatter)
        {
            var hex = formatter.FormatOffset(0).Length + 2;
            return (hex, hex + formatter.HexWidth + 2);
        }

        private void MeasureFont(double pixelsPerDip)
        {
            _pixelsPerDip = pixelsPerDip;
            var sample = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, Brushes.Black, pixelsPerDip);
            _charWidth = sample.WidthIncludingTrailingWhitespace;
            _rowHeight = Math.Ceiling(sample.Height + 2);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.ChangedButton != MouseButton.Left || _owner.Rows is not { } rows)
            {
                return;
            }

            var point = e.GetPosition(this);
            var index = (long)Math.Floor((point.Y - HeaderHeight) / _rowHeight);
            if (index < 0 || _owner._topRow + index >= rows.RowCount)
            {
                return;
            }

            _owner.Select(_owner._topRow + index, ColumnAt(point.X - Padding + _owner._horizontalOffset));
            e.Handled = true;
        }

        /// <summary>The byte under an x position: in the hex or ASCII column, else the first byte of the row.</summary>
        private int ColumnAt(double x)
        {
            var formatter = _owner._formatter;
            var (hexStart, asciiStart) = Columns(formatter);
            var character = x / _charWidth;
            if (character >= asciiStart)
            {
                return (int)Math.Min(formatter.Options.BytesPerLine - 1, Math.Floor(character - asciiStart));
            }

            if (character >= hexStart)
            {
                // The byte whose two digits are nearest the click, so clicks between groups still land.
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < formatter.Options.BytesPerLine; i++)
                {
                    var distance = Math.Abs(character - (hexStart + formatter.HexColumn(i) + 1));
                    if (distance < bestDistance)
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }

                return best;
            }

            return 0;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            // Precision touchpads send small deltas; still move at least one row per notch.
            var rows = -e.Delta * WheelRows / 120;
            _owner.ScrollBy(rows != 0 ? rows : -Math.Sign(e.Delta));
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            _owner.OnPaneKeyDown(e);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }
    }

    /// <summary>A strip beside the scroll bar marking where the differing rows are; click or drag to jump.</summary>
    private sealed class HexOverview : FrameworkElement
    {
        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(HexOverview), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LensBrushProperty = DependencyProperty.Register(
            nameof(LensBrush), typeof(Brush), typeof(HexOverview), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double Inset = 3;

        private readonly HexDiffControl _owner;

        public HexOverview(HexDiffControl owner)
        {
            _owner = owner;
            Width = 14;
            Cursor = Cursors.Hand;
            ToolTip = "Differences overview: click or drag to jump";
            SetResourceReference(TrackBrushProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            SetResourceReference(LensBrushProperty, EnvironmentColors.ScrollBarThumbBackgroundBrushKey);
        }

        public Brush TrackBrush
        {
            get => (Brush)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public Brush LensBrush
        {
            get => (Brush)GetValue(LensBrushProperty);
            set => SetValue(LensBrushProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(TrackBrush, null, new Rect(RenderSize));
            var rows = _owner.Rows;
            if (rows is null || rows.RowCount == 0 || ActualHeight <= 0)
            {
                return;
            }

            var scale = ActualHeight / rows.RowCount;
            dc.PushOpacity(0.45);
            dc.DrawRoundedRectangle(LensBrush, null, new Rect(1, _owner._topRow * scale, Math.Max(ActualWidth - 2, 1), Math.Max(_owner.VisibleRows * scale, 6)), 3, 3);
            dc.Pop();

            // One mark per run of differing rows, at least a pixel and a half tall so single rows show.
            var lastBottom = double.NegativeInfinity;
            foreach (var run in rows.Runs)
            {
                if (!run.HasDifference)
                {
                    continue;
                }

                var top = run.FirstRow * scale;
                var height = Math.Max(run.RowCount * scale, 1.5);
                if (top + height <= lastBottom)
                {
                    continue;
                }

                var brush = run.LeftCount == 0 ? DiffTheme.AddedBrush : run.RightCount == 0 ? DiffTheme.RemovedBrush : DiffTheme.ChangedBrush;
                dc.DrawRectangle(brush, null, new Rect(Inset, top, Math.Max(ActualWidth - (2 * Inset), 1), height));
                lastBottom = top + height;
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            JumpTo(e.GetPosition(this).Y);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured)
            {
                JumpTo(e.GetPosition(this).Y);
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            ReleaseMouseCapture();
        }

        private void JumpTo(double y)
        {
            if (_owner.Rows is not { } rows || ActualHeight <= 0)
            {
                return;
            }

            var row = (long)(Math.Max(0, Math.Min(1, y / ActualHeight)) * rows.RowCount);
            _owner.ScrollTo(row - (_owner.VisibleRows / 2));
        }
    }
}
