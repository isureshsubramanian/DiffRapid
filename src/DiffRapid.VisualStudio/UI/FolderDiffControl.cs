using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using System.Windows.Threading;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;
using DiffRapid.Core.Sync;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Folder comparison. While the scan runs, results stream into a flat list; when it finishes they are shown
/// as a collapsible tree in which every folder carries the combined status of its contents (the flat list
/// stays available). Double-clicking a file opens the file comparison.
/// </summary>
internal sealed class FolderDiffControl : DockPanel, IComparisonView
{
    private const double IndentPerLevel = 18;
    private const double ImpactBarWidth = 64;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(120);

    private static readonly (string Label, FolderCompareMethod Method)[] Methods =
    {
        ("Full contents", FolderCompareMethod.FullContents),
        ("Quick contents", FolderCompareMethod.QuickContents),
        ("Binary contents", FolderCompareMethod.BinaryContents),
        ("Modified date", FolderCompareMethod.Date),
        ("Date and size", FolderCompareMethod.DateAndSize),
        ("Size", FolderCompareMethod.Size),
        ("Existence", FolderCompareMethod.Existence),
        ("Hash (xxHash64)", FolderCompareMethod.Hash),
    };

    private readonly DiffRapidPackage _package;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly ConcurrentQueue<FolderCompareItem> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Dictionary<FolderCompareStatus, int> _counts = new();
    private readonly Dictionary<FolderCompareStatus, SummaryCard> _chips = new();
    private readonly Grid _composition = new() { ClipToBounds = true };
    private readonly TextBlock _compositionText = new() { FontSize = 11 };
    private List<FolderCompareItem> _streamed = new();
    private List<FolderCompareItem> _scanned = new();
    private readonly ListView _list = new();
    private readonly ToggleButton _treeToggle;
    private readonly ToggleButton _renamesToggle;
    private readonly Button _expandAll;
    private readonly Button _collapseAll;
    private readonly Button _copyToLeft;
    private readonly Button _copyToRight;
    private readonly Button _deleteLeft;
    private readonly Button _deleteRight;
    private readonly Button _exportReport;
    private readonly Button _createPatch;
    private readonly Button _sync;
    private readonly TextBox _search = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show only paths containing this text" };
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private ObservableCollection<FolderRow> _rows = new();
    private Node? _root;
    private FolderCompareResult? _result;
    private readonly FolderCompareOptions _baseOptions;
    private FolderCompareMethod _method;
    private CancellationTokenSource? _scanCancellation;
    private bool _scanning;
    private bool _refreshing;
    private bool _busy;
    private string? _afterScanMessage;
    private ViewState? _restoreAfterScan;
    private bool _disposed;

    public FolderDiffControl(DiffRapidPackage package, string leftRoot, string rightRoot, FolderCompareOptions options)
    {
        _package = package;
        _leftRoot = Path.GetFullPath(leftRoot);
        _rightRoot = Path.GetFullPath(rightRoot);
        _baseOptions = options;
        _method = options.Method;

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        _flushTimer = new DispatcherTimer(FlushInterval, DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flushTimer.Stop();

        _treeToggle = DiffTheme.OptionToggle("Tree", "Show folders as a tree (otherwise a flat list of files)", _ => ShowResults());
        _treeToggle.IsChecked = true;
        _treeToggle.Content = IconLabel(KnownMonikers.TreeView, "Tree");
        _renamesToggle = DiffTheme.OptionToggle(
            "Renames",
            "Pair files found on one side only whose contents match, and show them as renamed or moved",
            _ => StartScan(),
            options.DetectRenames);
        _expandAll = DiffTheme.CommandButton(Icon(KnownMonikers.ExpandAll), "Expand all folders", () => SetAllExpanded(true));
        _collapseAll = DiffTheme.CommandButton(Icon(KnownMonikers.CollapseAll), "Collapse all folders", () => SetAllExpanded(false));
        _copyToLeft = DiffTheme.CommandButton(IconLabel(KnownMonikers.Backwards, "Copy to left"), "Copy the selected files and folders from the right to the left (Ctrl+Shift+F8)", () => CopySelection(toRight: false));
        _copyToRight = DiffTheme.CommandButton(IconLabel(KnownMonikers.Forwards, "Copy to right"), "Copy the selected files and folders from the left to the right (Ctrl+F8)", () => CopySelection(toRight: true));
        _deleteLeft = DiffTheme.CommandButton(IconLabel(KnownMonikers.Delete, "Delete left"), "Move the selected files and folders on the left to the Recycle Bin (Delete, for items only on the left)", () => DeleteSelection(fromLeft: true));
        _deleteRight = DiffTheme.CommandButton(IconLabel(KnownMonikers.Delete, "Delete right"), "Move the selected files and folders on the right to the Recycle Bin (Delete, for items only on the right)", () => DeleteSelection(fromLeft: false));
        _exportReport = DiffTheme.CommandButton(IconLabel(KnownMonikers.Report, "Export report"), "Save the comparison as an HTML or CSV report", ExportReport);
        _exportReport.IsEnabled = false;
        _createPatch = DiffTheme.CommandButton(IconLabel(KnownMonikers.CompareFiles, "Create patch"), "Save the differences between the text files as one patch file (unified diff)", CreatePatch);
        _createPatch.IsEnabled = false;
        _sync = DiffTheme.CommandButton(IconLabel(KnownMonikers.Sync, "Sync"), "Synchronize the folders: mirror one side onto the other or copy newer files, with a preview first", Synchronize);
        _sync.IsEnabled = false;

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var header = BuildHeader();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var dashboard = BuildDashboard();
        SetDock(dashboard, Dock.Top);
        Children.Add(dashboard);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildList());

        _search.TextChanged += (_, _) => ShowResults();
        StartScan();
    }

    private bool TreeMode => _treeToggle.IsChecked == true;

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
        _flushTimer.Stop();
        _scanCancellation?.Cancel();
    }

    private FrameworkElement BuildToolbar()
    {
        var bar = new Grid { Margin = new Thickness(6, 4, 6, 4) };

        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Compare again (F5)", StartScan));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_copyToLeft);
        summary.Children.Add(_copyToRight);
        summary.Children.Add(_deleteLeft);
        summary.Children.Add(_deleteRight);
        summary.Children.Add(_exportReport);
        summary.Children.Add(_createPatch);
        summary.Children.Add(_sync);
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_verdict);
        bar.Children.Add(summary);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(_treeToggle);
        settings.Children.Add(_renamesToggle);
        settings.Children.Add(_expandAll);
        settings.Children.Add(_collapseAll);
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.Caption("FILTER"));
        _search.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
        settings.Children.Add(_search);
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.Caption("COMPARE BY"));
        var method = new ComboBox { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How files present on both sides are compared" };
        method.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var (label, _) in Methods)
        {
            method.Items.Add(label);
        }

        method.SelectedIndex = Math.Max(0, Array.FindIndex(Methods, m => m.Method == _method));
        method.SelectionChanged += (_, _) =>
        {
            _method = Methods[method.SelectedIndex].Method;
            StartScan();
        };
        settings.Children.Add(method);
        bar.Children.Add(settings);
        DiffTheme.WrapWhenNarrow(bar, summary, settings);

        var chrome = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        chrome.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.CommandBarGradientBeginColorKey);
        return chrome;
    }

    // Summary cards (which double as filters) and the composition bar, between the header and the list.
    private FrameworkElement BuildDashboard()
    {
        var cards = new UniformGrid { Rows = 1, Margin = new Thickness(6, 8, 6, 6) };
        AddCard(cards, FolderCompareStatus.Different, "Changed files", DiffTheme.ChangedBrush, "Files on both sides whose contents differ", shown: true);
        AddCard(cards, FolderCompareStatus.LeftOnly, "Only on the left", DiffTheme.RemovedBrush, "Files and folders only in the left folder; a folder counts once", shown: true);
        AddCard(cards, FolderCompareStatus.RightOnly, "Only on the right", DiffTheme.AddedBrush, "Files and folders only in the right folder; a folder counts once", shown: true);
        AddCard(cards, FolderCompareStatus.Renamed, "Renamed or moved", DiffTheme.MovedBrush, "Files only on one side whose content turns up under another name or folder on the other side", shown: true);
        AddCard(cards, FolderCompareStatus.Error, "Needs attention", DiffTheme.MovedBrush, "Items that could not be read, or are a file on one side and a folder on the other", shown: true);
        AddCard(cards, FolderCompareStatus.Identical, "Identical files", DiffTheme.IgnoredBrush, "Files that are the same on both sides (hidden in the list by default)", shown: false);

        var legend = new DockPanel { Margin = new Thickness(12, 0, 12, 10) };
        _composition.Height = 6;
        _composition.Margin = new Thickness(0, 0, 0, 4);
        _composition.ToolTip = "Share of all files: identical, changed, only on the left, only on the right";
        DockPanel.SetDock(_composition, Dock.Top);
        legend.Children.Add(_composition);
        _compositionText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        legend.Children.Add(_compositionText);

        var panel = new DockPanel();
        DockPanel.SetDock(cards, Dock.Top);
        panel.Children.Add(cards);
        panel.Children.Add(legend);

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    private void AddCard(Panel panel, FolderCompareStatus status, string label, Brush accent, string tooltip, bool shown)
    {
        var card = new SummaryCard(label, accent, tooltip, shown);
        card.Toggle.Checked += (_, _) => ShowResults();
        card.Toggle.Unchecked += (_, _) => ShowResults();
        _chips[status] = card;
        panel.Children.Add(card.Toggle);
    }

    /// <summary>Fills the composition bar with one segment per file status, sized by share.</summary>
    private void UpdateComposition(IReadOnlyDictionary<FolderCompareStatus, int> files)
    {
        var segments = new (FolderCompareStatus Status, Brush Brush, string Label)[]
        {
            (FolderCompareStatus.Identical, DiffTheme.Tint(DiffTheme.IgnoredBrush, 0x80), "identical"),
            (FolderCompareStatus.Different, DiffTheme.ChangedBrush, "changed"),
            (FolderCompareStatus.LeftOnly, DiffTheme.RemovedBrush, "only left"),
            (FolderCompareStatus.RightOnly, DiffTheme.AddedBrush, "only right"),
            (FolderCompareStatus.Renamed, DiffTheme.MovedBrush, "renamed or moved"),
            (FolderCompareStatus.Error, DiffTheme.MovedBrush, "need attention"),
        };

        _composition.Children.Clear();
        _composition.ColumnDefinitions.Clear();
        var total = segments.Sum(s => files.TryGetValue(s.Status, out var n) ? n : 0);
        var parts = new List<string>();
        foreach (var (status, brush, label) in segments)
        {
            var count = files.TryGetValue(status, out var n) ? n : 0;
            if (count == 0)
            {
                continue;
            }

            var column = _composition.ColumnDefinitions.Count;
            _composition.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            var segment = new Border { Background = brush, Margin = new Thickness(column == 0 ? 0 : 1, 0, 0, 0) };
            Grid.SetColumn(segment, column);
            _composition.Children.Add(segment);
            parts.Add($"{100.0 * count / total:0.#}% {label}");
        }

        _compositionText.Text = total == 0 ? string.Empty : $"{total:N0} files  ·  " + string.Join("  ·  ", parts);
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = RootLabel("LEFT", _leftRoot, DiffTheme.RemovedBrush);
        var right = RootLabel("RIGHT", _rightRoot, DiffTheme.AddedBrush);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var chrome = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 7, 10, 7) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    private static FrameworkElement RootLabel(string side, string path, Brush accent)
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
        panel.Children.Add(_details);

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        return chrome;
    }

    private UIElement BuildList()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Name", Width = 460, CellTemplate = NameTemplate(OnExpanderClicked) });
        view.Columns.Add(new GridViewColumn { Header = "Status", Width = 120, CellTemplate = StatusTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Impact", Width = ImpactBarWidth + 56, CellTemplate = ImpactTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Details", Width = 200, DisplayMemberBinding = new Binding(nameof(FolderRow.Details)) });
        view.Columns.Add(new GridViewColumn { Header = "Size", Width = 210, CellTemplate = SizeTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Left modified", Width = 130, CellTemplate = DateTemplate(nameof(FolderRow.LeftModified), nameof(FolderRow.LeftIsNewer)) });
        view.Columns.Add(new GridViewColumn { Header = "Right modified", Width = 130, CellTemplate = DateTemplate(nameof(FolderRow.RightModified), nameof(FolderRow.RightIsNewer)) });

        _list.View = view;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode = SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => UpdateActionButtons();
        _list.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogListViewStyleKey);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, VsResourceKeys.ThemedDialogListViewItemGridStyleKey);
        _list.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.ToolWindowBackgroundColorKey);
        _list.Loaded += (_, _) => view.ColumnHeaderContainerStyle = _list.TryFindResource(VsResourceKeys.ThemedDialogGridViewColumnHeaderStyleKey) as Style;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListViewItem)
            {
                Activate();
            }
        };
        _list.PreviewKeyDown += OnListKeyDown;
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenu.SetResourceReference(StyleProperty, VsResourceKeys.ContextMenuStyleKey);
        _list.ContextMenuOpening += OnContextMenuOpening;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                StartScan();
                e.Handled = true;
            }
        };
        SetRows(new ObservableCollection<FolderRow>());
        return _list;
    }

    private static DataTemplate NameTemplate(MouseButtonEventHandler onExpander)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetBinding(MarginProperty, new Binding(nameof(FolderRow.Indent)));
        panel.SetBinding(ToolTipProperty, new Binding(nameof(FolderRow.ToolTip)));

        var expander = new FrameworkElementFactory(typeof(TextBlock));
        expander.SetValue(WidthProperty, 14.0);
        expander.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        expander.SetValue(TextBlock.BackgroundProperty, Brushes.Transparent);
        expander.SetValue(CursorProperty, Cursors.Hand);
        expander.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Expander)));
        expander.SetBinding(VisibilityProperty, new Binding(nameof(FolderRow.ExpanderVisibility)));
        expander.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        expander.AddHandler(UIElement.MouseLeftButtonDownEvent, onExpander);

        var icon = new FrameworkElementFactory(typeof(CrispImage));
        icon.SetValue(WidthProperty, 16.0);
        icon.SetValue(HeightProperty, 16.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        icon.SetBinding(CrispImage.MonikerProperty, new Binding(nameof(FolderRow.Icon)));

        var folder = new FrameworkElementFactory(typeof(TextBlock));
        folder.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Folder)));
        folder.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Name)));

        panel.AppendChild(expander);
        panel.AppendChild(icon);
        panel.AppendChild(folder);
        panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }

    // A soft pill in the status colour.
    private static DataTemplate StatusTemplate()
    {
        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        pill.SetValue(Border.PaddingProperty, new Thickness(8, 1, 8, 1));
        pill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(FolderRow.StatusTint)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.StatusText)));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(FolderRow.StatusBrush)));
        pill.AppendChild(text);
        return new DataTemplate { VisualTree = pill };
    }

    // A thin bar on a faint track, plus the share of changed files for folders.
    private static DataTemplate ImpactTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(WidthProperty, ImpactBarWidth);
        track.SetValue(HeightProperty, 6.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        track.SetValue(Border.BackgroundProperty, DiffTheme.Tint(DiffTheme.IgnoredBrush, 0x30));
        track.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bar.SetBinding(WidthProperty, new Binding(nameof(FolderRow.ImpactWidth)));
        bar.SetBinding(Border.BackgroundProperty, new Binding(nameof(FolderRow.StatusBrush)));
        track.AppendChild(bar);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(MarginProperty, new Thickness(6, 0, 0, 0));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.ImpactText)));
        panel.AppendChild(track);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    private static DataTemplate SizeTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.SizeText)));
        var delta = new FrameworkElementFactory(typeof(TextBlock));
        delta.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        delta.SetValue(TextBlock.FontSizeProperty, 11.0);
        delta.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        delta.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        delta.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.SizeDelta)));
        delta.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(FolderRow.SizeDeltaBrush)));
        panel.AppendChild(text);
        panel.AppendChild(delta);
        return new DataTemplate { VisualTree = panel };
    }

    // The newer of the two dates is shown in bold.
    private static DataTemplate DateTemplate(string property, string isNewerProperty)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        var newer = new DataTrigger { Binding = new Binding(isNewerProperty), Value = true };
        newer.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        var template = new DataTemplate { VisualTree = text };
        template.Triggers.Add(newer);
        return template;
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void SetRows(ObservableCollection<FolderRow> rows)
    {
        _rows = rows;
        _list.ItemsSource = rows;
    }

    private void StartScan()
    {
        if (_disposed)
        {
            return;
        }

        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        while (_pending.TryDequeue(out _))
        {
        }

        // A rescan (after a copy, F5...) keeps the current results on screen and swaps them for the new ones
        // when the scan is done, keeping the user's place; only the first scan streams rows in as they come.
        _refreshing = _root is not null;
        _restoreAfterScan ??= CaptureViewState();
        _scanned = new List<FolderCompareItem>();
        _scanning = true;
        UpdateActionButtons();
        _flushTimer.Start();
        if (_refreshing)
        {
            _position.Text = "Comparing again…";
        }
        else
        {
            _streamed = _scanned;
            _root = null;
            _counts.Clear();
            SetRows(new ObservableCollection<FolderRow>());
            UpdateChips();
            UpdateComposition(new Dictionary<FolderCompareStatus, int>());
            _expandAll.IsEnabled = false;
            _collapseAll.IsEnabled = false;
            _verdict.Text = "Comparing…";
            _verdict.ClearValue(TextBlock.ForegroundProperty);
            _position.Text = "Scanning folders…";
            _details.Text = string.Empty;
        }

        var options = _baseOptions with { Method = _method, DetectRenames = _renamesToggle.IsChecked == true };
        var reporter = new QueueReporter(_pending, cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var result = await Task.Run(() => FolderComparer.Compare(_leftRoot, _rightRoot, options, reporter, cancellation.Token), cancellation.Token);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    Complete(result, stopwatch.Elapsed);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!cancellation.IsCancellationRequested)
                {
                    _scanning = false;
                    _refreshing = false;
                    _flushTimer.Stop();
                    UpdateChips();
                    UpdateActionButtons();
                    _verdict.Text = "Compare failed";
                    _verdict.Foreground = DiffTheme.RemovedBrush;
                    _position.Text = ex.Message;
                }
            }
        }).FileAndForget("DiffRapid/FolderCompare");
    }

    // While scanning: move reported files into the flat list in small batches so the UI stays responsive.
    private void Flush()
    {
        var added = 0;
        while (_pending.TryDequeue(out var item))
        {
            if (!IsListedFlat(item))
            {
                continue;
            }

            _scanned.Add(item);
            added++;
            if (_refreshing)
            {
                continue;
            }

            var status = ChipStatus(item.Status);
            _counts[status] = _counts.TryGetValue(status, out var count) ? count + 1 : 1;
            var row = new FolderRow(item, null, depth: 0, treeMode: false);
            if (IsShown(row))
            {
                _rows.Add(row);
            }
        }

        if (added > 0 && _scanning && !_refreshing)
        {
            UpdateChips();
            _position.Text = $"Scanning folders…  {_scanned.Count:N0} items so far";
        }
    }

    private void Complete(FolderCompareResult result, TimeSpan elapsed)
    {
        _scanning = false;
        _flushTimer.Stop();
        Flush();
        _refreshing = false;
        _streamed = _scanned;

        _root = Node.Build(result.Root);
        _result = result;
        _counts.Clear();
        foreach (var pair in _root.Summary)
        {
            _counts[pair.Key] = pair.Value;
        }

        UpdateChips();
        UpdateComposition(_root.Files);
        RestoreViewState();
        ShowResults();
        RestoreSelectionAndScroll();

        var changes = Count(FolderCompareStatus.Different) + Count(FolderCompareStatus.LeftOnly) + Count(FolderCompareStatus.RightOnly) + Count(FolderCompareStatus.Renamed) + Count(FolderCompareStatus.Error);
        if (result.Root.Error is not null)
        {
            _verdict.Text = "Compare failed";
            _verdict.Foreground = DiffTheme.RemovedBrush;
            _position.Text = result.Root.Error;
        }
        else if (changes == 0)
        {
            _verdict.Text = "✔  Folders are identical";
            _verdict.Foreground = DiffTheme.AddedBrush;
            _position.Text = $"{Count(FolderCompareStatus.Identical):N0} identical files";
        }
        else
        {
            _verdict.Text = changes == 1 ? "1 difference" : $"{changes:N0} differences";
            _verdict.ClearValue(TextBlock.ForegroundProperty);
            _position.Text = "Double-click a file to compare it  ·  select items to copy or delete them  ·  F5 compares again";
        }

        if (_afterScanMessage is not null)
        {
            _position.Text = _afterScanMessage;
            _afterScanMessage = null;
        }

        UpdateActionButtons();

        var skipped = result.Counts.TryGetValue(FolderCompareStatus.Skipped, out var s) ? s : 0;
        var method = Methods.First(m => m.Method == _method).Label;
        _details.Text = $"Compared by {method.ToLowerInvariant()}  ·  {result.Items.Count - skipped:N0} items"
            + (skipped > 0 ? $" ({skipped:N0} excluded)" : string.Empty)
            + $"  ·  {FormatElapsed(elapsed)}";
    }

    /// <summary>Rebuilds the visible rows from the current mode, chips and search text.</summary>
    private void ShowResults()
    {
        if (_chips.Count < 5)
        {
            return;
        }

        var tree = TreeMode && _root is not null;
        _expandAll.IsEnabled = tree;
        _collapseAll.IsEnabled = tree;

        var rows = new ObservableCollection<FolderRow>();
        if (tree)
        {
            var searching = _search.Text.Trim().Length > 0;
            _root!.UpdateVisibility(IsShown);
            AppendChildren(rows, _root, forceExpand: searching);
        }
        else
        {
            foreach (var item in _streamed.OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var row = new FolderRow(item, null, depth: 0, treeMode: false);
                if (IsShown(row))
                {
                    rows.Add(row);
                }
            }
        }

        SetRows(rows);
    }

    private static void AppendChildren(IList<FolderRow> rows, Node node, bool forceExpand)
    {
        foreach (var child in node.Children)
        {
            if (!child.Visible)
            {
                continue;
            }

            rows.Add(child.Row);
            if (child.Row.IsExpanded || (forceExpand && child.Children.Count > 0))
            {
                AppendChildren(rows, child, forceExpand);
            }
        }
    }

    // A row passes the filters when its status chip is on and its path contains the search text.
    private bool IsShown(FolderRow row)
    {
        if (_chips.TryGetValue(ChipStatus(row.Item.Status), out var chip) && !chip.Shown)
        {
            return false;
        }

        var search = _search.Text.Trim();
        return search.Length == 0 || row.Item.RelativePath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static FolderCompareStatus ChipStatus(FolderCompareStatus status) =>
        status == FolderCompareStatus.TypeConflict ? FolderCompareStatus.Error
        : status == FolderCompareStatus.Moved ? FolderCompareStatus.Renamed
        : status;

    // Flat list: files, plus folders that exist on one side or cannot be compared.
    private static bool IsListedFlat(FolderCompareItem item) =>
        item.RelativePath.Length != 0
        && item.Status != FolderCompareStatus.Skipped
        && (item.Kind != FolderItemKind.Folder
            || item.Status is FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly or FolderCompareStatus.TypeConflict or FolderCompareStatus.Error);

    private int Count(FolderCompareStatus status) => _counts.TryGetValue(status, out var count) ? count : 0;

    private void UpdateChips()
    {
        foreach (var pair in _chips)
        {
            pair.Value.SetCount(Count(pair.Key), CardDetail(pair.Key));
        }
    }

    private string CardDetail(FolderCompareStatus status)
    {
        if (_scanning && !_refreshing)
        {
            return "Scanning…";
        }

        if (_root is null)
        {
            return string.Empty;
        }

        var files = _root.Files.TryGetValue(status, out var n) ? n : 0;
        var bytes = _root.Bytes.TryGetValue(status, out var b) ? b : 0;
        return status switch
        {
            FolderCompareStatus.Different => _root.TotalFiles == 0 ? string.Empty : $"{100.0 * files / _root.TotalFiles:0.#}% of {_root.TotalFiles:N0} files",
            FolderCompareStatus.Error => files == 0 ? "Nothing to review" : "Unreadable, or file vs folder",
            _ => files == 0 ? "None" : $"{files:N0} {(files == 1 ? "file" : "files")}  ·  {FormatSize(bytes)}",
        };
    }

    private void OnExpanderClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderRow { HasChildren: true } row })
        {
            SetExpanded(row, !row.IsExpanded);
            e.Handled = true;
        }
    }

    // Expanding inserts the visible descendants below the row; collapsing removes them. The scroll position is kept.
    private void SetExpanded(FolderRow row, bool expanded)
    {
        if (row.Node is null || row.IsExpanded == expanded || !row.HasChildren)
        {
            return;
        }

        row.IsExpanded = expanded;
        var index = _rows.IndexOf(row);
        if (index < 0 || !TreeMode || _search.Text.Trim().Length > 0)
        {
            return;
        }

        if (expanded)
        {
            var inserted = new List<FolderRow>();
            AppendChildren(inserted, row.Node, forceExpand: false);
            for (var i = 0; i < inserted.Count; i++)
            {
                _rows.Insert(index + 1 + i, inserted[i]);
            }
        }
        else
        {
            while (index + 1 < _rows.Count && _rows[index + 1].Depth > row.Depth)
            {
                _rows.RemoveAt(index + 1);
            }
        }
    }

    private void SetAllExpanded(bool expanded)
    {
        if (_root is null)
        {
            return;
        }

        var selected = _list.SelectedItem;
        _root.SetExpandedRecursive(expanded);
        ShowResults();
        if (selected is FolderRow row && _rows.Contains(row))
        {
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (_list.SelectedItem is not FolderRow row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F8 when Keyboard.Modifiers == ModifierKeys.Control:
                CopySelection(toRight: true);
                e.Handled = true;
                break;
            case Key.F8 when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                CopySelection(toRight: false);
                e.Handled = true;
                break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.Enter:
                Activate();
                e.Handled = true;
                break;
            case Key.Right when row.HasChildren && !row.IsExpanded:
                SetExpanded(row, true);
                e.Handled = true;
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                SetExpanded(row, false);
                e.Handled = true;
                break;
            case Key.Left when row.Node?.Parent?.Row is { } parent && _rows.Contains(parent):
                _list.SelectedItem = parent;
                _list.ScrollIntoView(parent);
                (_list.ItemContainerGenerator.ContainerFromItem(parent) as ListViewItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    // Folders toggle; files open in a comparison (or on their own when only one side has them).
    private void Activate()
    {
        if (_list.SelectedItem is not FolderRow row)
        {
            return;
        }

        if (row.HasChildren)
        {
            SetExpanded(row, !row.IsExpanded);
            return;
        }

        if (row.Item.Kind != FolderItemKind.File)
        {
            return;
        }

        var left = FullPath(row.Item, left: true);
        var right = FullPath(row.Item, left: false);

        // A renamed or moved file is compared with its counterpart, the other side's file under its own name.
        var paired = row.Item.Status is FolderCompareStatus.Renamed or FolderCompareStatus.Moved && row.Item.CounterpartPath is not null;
        if (paired)
        {
            var counterpart = row.Item with { RelativePath = row.Item.CounterpartPath! };
            if (row.Item.Left.Exists)
            {
                right = FullPath(counterpart, left: false);
            }
            else
            {
                left = FullPath(counterpart, left: true);
            }
        }

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (paired || (row.Item.Left.Exists && row.Item.Right.Exists))
                {
                    await _package.OpenComparisonAsync(left, right);
                }
                else
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.OpenDocument(_package, row.Item.Left.Exists ? left : right);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.LogError(ex);
                _position.Text = ex.Message;
            }
        }).FileAndForget("DiffRapid/FolderOpen");
    }

    private ViewState? CaptureViewState()
    {
        if (_root is null)
        {
            return null;
        }

        var expanded = new HashSet<string>(
            _root.Descendants().Where(n => n.Row.IsExpanded).Select(n => n.Item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(SelectedItems().Select(i => i.RelativePath), StringComparer.OrdinalIgnoreCase);

        // The list scrolls by item, so the offset is the index of the top row.
        var offset = FindScrollViewer(_list)?.VerticalOffset ?? 0;
        var top = (int)offset;
        var topPath = top >= 0 && top < _rows.Count ? _rows[top].Item.RelativePath : null;
        return new ViewState(expanded, selected, topPath, offset);
    }

    // Opens the folders that were open before the rescan (the new tree starts collapsed).
    private void RestoreViewState()
    {
        if (_restoreAfterScan is not { } state || _root is null)
        {
            return;
        }

        foreach (var node in _root.Descendants())
        {
            if (node.Row.HasChildren && state.Expanded.Contains(node.Item.RelativePath))
            {
                node.Row.IsExpanded = true;
            }
        }
    }

    // Reselects the rows that are still listed and scrolls back to the same top row (or the same offset).
    private void RestoreSelectionAndScroll()
    {
        if (_restoreAfterScan is not { } state)
        {
            return;
        }

        _restoreAfterScan = null;
        _list.SelectedItems.Clear();
        foreach (var row in _rows.Where(r => state.Selected.Contains(r.Item.RelativePath)))
        {
            _list.SelectedItems.Add(row);
        }

        var top = -1;
        for (var i = 0; state.TopPath is not null && i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Item.RelativePath, state.TopPath, StringComparison.OrdinalIgnoreCase))
            {
                top = i;
                break;
            }
        }

        // Lay out the new rows first so the scroll range covers the offset.
        _list.UpdateLayout();
        FindScrollViewer(_list)?.ScrollToVerticalOffset(top >= 0 ? top : state.Offset);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private IReadOnlyList<FolderCompareItem> SelectedItems() => _list.SelectedItems.OfType<FolderRow>().Select(r => r.Item).ToList();

    private string FullPath(FolderCompareItem item, bool left) =>
        Path.Combine(left ? _leftRoot : _rightRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    // Right-click menu for the selection, rebuilt on each opening with only the actions that apply to it.
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = SelectedItems();
        var menu = _list.ContextMenu;
        menu.Items.Clear();
        if (selected.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var ready = !_scanning && !_busy && _root is not null;
        var single = selected.Count == 1 ? selected[0] : null;
        var groups = new List<List<MenuItem>>();

        var open = new List<MenuItem>();
        if (single is { Kind: FolderItemKind.File })
        {
            var both = single.Left.Exists && single.Right.Exists;
            open.Add(MenuEntry(both ? "Compare" : "Open", KnownMonikers.Open, "Enter", Activate));
        }

        groups.Add(open);

        var copy = new List<MenuItem>();
        if (ready && !FolderCopyPlan.Create(selected, toRight: true).IsEmpty)
        {
            copy.Add(MenuEntry("Copy to right", KnownMonikers.Forwards, "Ctrl+F8", () => CopySelection(toRight: true)));
        }

        if (ready && !FolderCopyPlan.Create(selected, toRight: false).IsEmpty)
        {
            copy.Add(MenuEntry("Copy to left", KnownMonikers.Backwards, "Ctrl+Shift+F8", () => CopySelection(toRight: false)));
        }

        groups.Add(copy);

        var delete = new List<MenuItem>();
        var onlySide = FolderDeletePlan.OnlySide(selected);
        if (ready && !FolderDeletePlan.Create(selected, fromLeft: true).IsEmpty)
        {
            delete.Add(MenuEntry("Delete from left…", KnownMonikers.Delete, onlySide == true ? "Del" : null, () => DeleteSelection(fromLeft: true)));
        }

        if (ready && !FolderDeletePlan.Create(selected, fromLeft: false).IsEmpty)
        {
            delete.Add(MenuEntry("Delete from right…", KnownMonikers.Delete, onlySide == false ? "Del" : null, () => DeleteSelection(fromLeft: false)));
        }

        groups.Add(delete);

        var paths = new List<MenuItem>();
        if (single is not null)
        {
            // The side the entry is on; the left when it is on both.
            var path = FullPath(single, left: single.Left.Exists);
            paths.Add(MenuEntry("Open containing folder", KnownMonikers.OpenFolder, null, () => RevealInExplorer(path)));
            paths.Add(MenuEntry("Copy full path", KnownMonikers.Copy, null, () => CopyToClipboard(path)));
        }

        groups.Add(paths);

        foreach (var group in groups.Where(g => g.Count > 0))
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            foreach (var item in group)
            {
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    private static MenuItem MenuEntry(string header, ImageMoniker icon, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header, Icon = Icon(icon), InputGestureText = gesture ?? string.Empty };
        item.Click += (_, _) => action();
        return item;
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _position.Text = "Copied " + text;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another application can hold the clipboard open.
            _position.Text = $"Could not copy the path: {ex.Message}";
        }
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _position.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void UpdateActionButtons()
    {
        var enabled = !_scanning && !_busy && _root is not null && _list.SelectedItems.Count > 0;
        _copyToLeft.IsEnabled = enabled;
        _copyToRight.IsEnabled = enabled;
        _deleteLeft.IsEnabled = enabled;
        _deleteRight.IsEnabled = enabled;
        _exportReport.IsEnabled = !_scanning && !_busy && _result is not null;
        _createPatch.IsEnabled = _exportReport.IsEnabled;
        _sync.IsEnabled = _exportReport.IsEnabled;
    }

    // Saves the last completed comparison as a report; the save dialog's file type picks the format and rows.
    private void ExportReport()
    {
        if (_scanning || _busy || _result is null)
        {
            return;
        }

        // The file name cannot hold a folder separator, so the parent and folder names are joined by a space there.
        var names = ReportNames();
        var (left, right) = (names.Left.Replace(Path.DirectorySeparatorChar, ' '), names.Right.Replace(Path.DirectorySeparatorChar, ' '));
        var dialog = new SaveFileDialog
        {
            Title = "Export folder comparison report",
            FileName = string.Concat((left + " vs " + right + " report")
                .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)),
            Filter = "HTML report, differences only (*.html)|*.html"
                + "|HTML report, all files (*.html)|*.html"
                + "|CSV, differences only (*.csv)|*.csv"
                + "|CSV, all files (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var html = dialog.FilterIndex <= 2;
        var options = new FolderReportOptions
        {
            LeftRoot = _leftRoot,
            RightRoot = _rightRoot,
            Title = names.Left + " ↔ " + names.Right,
            GeneratedAt = DateTimeOffset.Now,
            MethodLabel = Methods.First(m => m.Method == _method).Label,
            IncludeIdentical = dialog.FilterIndex % 2 == 0,
        };

        try
        {
            // CSV gets a byte order mark so Excel reads non-ASCII paths correctly.
            using (var writer = new StreamWriter(dialog.FileName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: !html)))
            {
                if (html)
                {
                    FolderReportWriter.WriteHtml(_result, options, writer);
                }
                else
                {
                    FolderReportWriter.WriteCsv(_result, options, writer);
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

    // One git-style patch for every changed, added and removed text file; it always compares exact content.
    private void CreatePatch()
    {
        if (_scanning || _busy || _result is null)
        {
            return;
        }

        var result = _result;
        var names = ReportNames();
        var message = PatchExport.Save(
            names.Left.Replace(Path.DirectorySeparatorChar, ' ') + " to " + names.Right.Replace(Path.DirectorySeparatorChar, ' '),
            writer => FolderPatchWriter.Write(writer, result, DiffRapid.Core.Comparison.CompareOptions.Default));
        if (message is not null)
        {
            _position.Text = message;
        }
    }

    // Previews a sync profile over the current results, then runs it; deletions go to the Recycle Bin.
    private void Synchronize()
    {
        if (_scanning || _busy || _result is null)
        {
            return;
        }

        var dialog = new SyncDialog(_result);
        if (dialog.ShowModal() != true || dialog.Plan is not { } plan)
        {
            return;
        }

        var options = new SyncExecuteOptions { Delete = RecycleDelete };
        RunFileOperation(
            "Synchronizing…",
            cancellation => SyncExecutor.Execute(plan, null, options, cancellation)
                .Where(r => r.Status == SyncOperationStatus.Failed)
                .Select(r => $"{r.Operation.RelativePath}: {r.Error}")
                .ToList(),
            "Sync",
            errors => errors == 0
                ? $"Synchronized: {Plural(plan.Operations.Count(o => o.ChangesFiles), "operation")}."
                : $"Synchronized with {errors:N0} {(errors == 1 ? "failure" : "failures")}");
    }

    private static void RecycleDelete(string path, bool isFolder)
    {
        if (isFolder)
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
        else
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }

    // Folders with the same name are told apart by their parent folder, e.g. "Xls" vs "Xls-10272026\Xls".
    private (string Left, string Right) ReportNames()
    {
        var left = FolderName(_leftRoot);
        var right = FolderName(_rightRoot);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? (WithParent(_leftRoot), WithParent(_rightRoot))
            : (left, right);
    }

    private static string WithParent(string path)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) ? FolderName(path) : Path.Combine(FolderName(parent!), FolderName(path));
    }

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    // Copies the selected entries to the other side after a confirmation, then compares again.
    private void CopySelection(bool toRight)
    {
        if (_scanning || _busy || _root is null)
        {
            return;
        }

        var plan = FolderCopyPlan.Create(SelectedItems(), toRight);
        var (from, to) = toRight ? ("left", "right") : ("right", "left");
        if (plan.IsEmpty)
        {
            _position.Text = $"Nothing to copy: the selection has no files on the {from} that differ or are missing on the {to}.";
            return;
        }

        var (sourceRoot, targetRoot) = toRight ? (_leftRoot, _rightRoot) : (_rightRoot, _leftRoot);
        var files = plan.Files.Count;
        var added = files - plan.Overwrites;
        var message = $"Copy {Plural(files, "file")} from the {from} to the {to}?"
            + Environment.NewLine + Environment.NewLine
            + (added > 0 ? $"{added:N0} new" : string.Empty)
            + (added > 0 && plan.Overwrites > 0 ? ", " : string.Empty)
            + (plan.Overwrites > 0 ? $"{plan.Overwrites:N0} replaced (the {to} version is overwritten)" : string.Empty)
            + Environment.NewLine + Environment.NewLine
            + "Into: " + targetRoot;
        if (!Confirm(message, "DiffRapid: Copy to " + to, warn: plan.Overwrites > 0))
        {
            return;
        }

        RunFileOperation(
            $"Copying {Plural(files, "file")} to the {to}…",
            token => plan.Run(sourceRoot, targetRoot, token),
            "Copy",
            errors => errors == 0
                ? $"Copied {Plural(files, "file")} to the {to}."
                : $"Copied {files - errors:N0} of {Plural(files, "file")} to the {to}; {errors:N0} failed");
    }

    // The Delete key: removes items that exist on one side only from that side; a mixed selection needs a side.
    private void DeleteSelection()
    {
        var side = FolderDeletePlan.OnlySide(SelectedItems());
        if (side is { } fromLeft)
        {
            DeleteSelection(fromLeft);
        }
        else if (_list.SelectedItems.Count > 0)
        {
            _position.Text = "The selection exists on both sides: use Delete left or Delete right.";
        }
    }

    // Moves the selected entries on one side to the Recycle Bin after a confirmation, then compares again.
    private void DeleteSelection(bool fromLeft)
    {
        if (_scanning || _busy || _root is null)
        {
            return;
        }

        var plan = FolderDeletePlan.Create(SelectedItems(), fromLeft);
        var side = fromLeft ? "left" : "right";
        if (plan.IsEmpty)
        {
            _position.Text = $"Nothing to delete: the selection does not exist on the {side}.";
            return;
        }

        var root = fromLeft ? _leftRoot : _rightRoot;
        var what = string.Join(" and ", new[]
        {
            plan.Files > 0 ? Plural(plan.Files, "file") : null,
            plan.Folders > 0 ? Plural(plan.Folders, "folder") + " (with everything in " + (plan.Folders == 1 ? "it" : "them") + ")" : null,
        }.Where(p => p is not null));
        var message = $"Move {what} on the {side} to the Recycle Bin?"
            + Environment.NewLine + Environment.NewLine
            + "From: " + root;
        if (!Confirm(message, "DiffRapid: Delete " + side, warn: true))
        {
            return;
        }

        RunFileOperation(
            $"Deleting {what} on the {side}…",
            token => plan.Run(root, token),
            "Delete",
            errors => errors == 0
                ? $"Moved {what} on the {side} to the Recycle Bin."
                : $"Deleted on the {side} with {errors:N0} {(errors == 1 ? "failure" : "failures")}");
    }

    private bool Confirm(string message, string title, bool warn) =>
        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            title,
            warn ? OLEMSGICON.OLEMSGICON_WARNING : OLEMSGICON.OLEMSGICON_QUERY,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == (int)VSConstants.MessageBoxResult.IDOK;

    // Runs a copy or delete off the UI thread, logs per-item failures, then compares again and reports.
    private void RunFileOperation(string busyText, Func<CancellationToken, IReadOnlyList<string>> work, string verb, Func<int, string> outcome)
    {
        _busy = true;
        UpdateActionButtons();
        _position.Text = busyText;
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            IReadOnlyList<string> errors;
            try
            {
                errors = await Task.Run(() => work(_package.DisposalToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _busy = false;
            }

            if (_disposed)
            {
                return;
            }

            foreach (var error in errors)
            {
                Diagnostics.WriteLine($"[{DateTime.Now:HH:mm:ss}] {verb} failed: {error}");
            }

            _afterScanMessage = errors.Count == 0
                ? outcome(0)
                : $"{outcome(errors.Count)} (details in the DiffRapid output pane): {errors[0]}";
            StartScan();
        }).FileAndForget("DiffRapid/Folder" + verb);
    }

    private static string Plural(int count, string singular) => $"{count:N0} {(count == 1 ? singular : singular + "s")}";

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000 ? $"{Math.Max(1, (int)elapsed.TotalMilliseconds)} ms" : $"{elapsed.TotalSeconds:0.0} s";

    /// <summary>What the list showed before a rescan: open folders, selected rows and the top row, by relative path.</summary>
    private sealed class ViewState
    {
        public ViewState(HashSet<string> expanded, HashSet<string> selected, string? topPath, double offset)
        {
            Expanded = expanded;
            Selected = selected;
            TopPath = topPath;
            Offset = offset;
        }

        public HashSet<string> Expanded { get; }

        public HashSet<string> Selected { get; }

        public string? TopPath { get; }

        public double Offset { get; }
    }

    /// <summary>
    /// A dashboard tile: accent stripe, headline count, label and a detail line. It doubles as a filter;
    /// switching it off dims the tile and hides those rows.
    /// </summary>
    private sealed class SummaryCard
    {
        private static readonly Lazy<Style> CardStyle = new(CreateStyle);

        private readonly TextBlock _count = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _detail = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };

        public SummaryCard(string label, Brush accent, string tooltip, bool shown)
        {
            var stripe = new Border { Width = 28, Height = 3, CornerRadius = new CornerRadius(2), Background = accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            var caption = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
            _detail.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);

            var content = new StackPanel();
            content.Children.Add(stripe);
            content.Children.Add(_count);
            content.Children.Add(caption);
            content.Children.Add(_detail);
            Toggle = new ToggleButton { Content = content, ToolTip = tooltip + ". Click to show or hide these rows.", Style = CardStyle.Value, IsChecked = shown };
            SetCount(0, string.Empty);
        }

        public ToggleButton Toggle { get; }

        public bool Shown => Toggle.IsChecked == true;

        public void SetCount(int count, string detail)
        {
            _count.Text = count.ToString("N0", CultureInfo.CurrentCulture);
            _detail.Text = detail;
        }

        private static Style CreateStyle()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
            border.SetValue(SnapsToDevicePixelsProperty, true);
            border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

            var style = new Style(typeof(ToggleButton));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToggleButton)) { VisualTree = border }));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarGradientBeginBrushKey)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowBorderBrushKey)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowTextBrushKey)));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(6, 0, 6, 0)));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(FocusVisualStyleProperty, null));

            var off = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = false };
            off.Setters.Add(new Setter(OpacityProperty, 0.45));
            style.Triggers.Add(off);
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarSelectedBorderBrushKey)));
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey)));
            style.Triggers.Add(hover);
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarSelectedBorderBrushKey)));
            style.Triggers.Add(focus);

            style.Seal();
            return style;
        }
    }

    /// <summary>Collects scan results from worker threads; the UI drains the queue on a timer.</summary>
    private sealed class QueueReporter : IProgress<FolderCompareItem>
    {
        private readonly ConcurrentQueue<FolderCompareItem> _queue;
        private readonly CancellationToken _token;

        public QueueReporter(ConcurrentQueue<FolderCompareItem> queue, CancellationToken token)
        {
            _queue = queue;
            _token = token;
        }

        public void Report(FolderCompareItem value)
        {
            if (!_token.IsCancellationRequested)
            {
                _queue.Enqueue(value);
            }
        }
    }

    /// <summary>
    /// One entry of the result tree. A folder that exists on one side only is a single entry for counting;
    /// its contents are still browsable below it.
    /// </summary>
    private sealed class Node
    {
        private Node(FolderCompareItem item, Node? parent, int depth)
        {
            Item = item;
            Parent = parent;
            Depth = depth;
        }

        public FolderCompareItem Item { get; }

        public Node? Parent { get; }

        public int Depth { get; }

        public List<Node> Children { get; } = new();

        /// <summary>Counted entries below this node, by chip status.</summary>
        public Dictionary<FolderCompareStatus, int> Summary { get; } = new();

        /// <summary>Files below a one-sided folder.</summary>
        public int FileCount { get; private set; }

        /// <summary>Files below this node by status; a one-sided folder counts each file inside it.</summary>
        public Dictionary<FolderCompareStatus, int> Files { get; } = new();

        /// <summary>Bytes of the files in <see cref="Files"/> (the larger side when both exist).</summary>
        public Dictionary<FolderCompareStatus, long> Bytes { get; } = new();

        public long LeftBytes { get; private set; }

        public long RightBytes { get; private set; }

        public int TotalFiles => Files.Values.Sum();

        public int ChangedFiles => TotalFiles - (Files.TryGetValue(FolderCompareStatus.Identical, out var n) ? n : 0);

        public FolderRow Row { get; private set; } = null!;

        public bool Visible { get; private set; }

        public static Node Build(FolderCompareItem rootItem)
        {
            var root = new Node(rootItem, null, -1);
            root.AddChildren();
            return root;
        }

        public void UpdateVisibility(Func<FolderRow, bool> isShown)
        {
            var anyChild = false;
            foreach (var child in Children)
            {
                child.UpdateVisibility(isShown);
                anyChild |= child.Visible;
            }

            // Folders on both sides are shown when something inside them is; everything else follows the filters.
            var isMixedFolder = Item.Kind == FolderItemKind.Folder && Item.Left.Exists && Item.Right.Exists
                && Item.Status is FolderCompareStatus.Identical or FolderCompareStatus.Different;
            Visible = Depth < 0 || (isMixedFolder ? anyChild : isShown(Row) || anyChild);
        }

        public IEnumerable<Node> Descendants()
        {
            foreach (var child in Children)
            {
                yield return child;
                foreach (var descendant in child.Descendants())
                {
                    yield return descendant;
                }
            }
        }

        public void SetExpandedRecursive(bool expanded)
        {
            foreach (var child in Children)
            {
                if (child.Row.HasChildren)
                {
                    child.Row.IsExpanded = expanded;
                }

                child.SetExpandedRecursive(expanded);
            }
        }

        private void AddChildren()
        {
            var ordered = Item.Children
                .Where(c => c.Status != FolderCompareStatus.Skipped)
                .OrderBy(c => c.Kind == FolderItemKind.Folder ? 0 : 1)
                .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var childItem in ordered)
            {
                var child = new Node(childItem, this, Depth + 1);
                child.AddChildren();
                Children.Add(child);

                var isMixedFolder = childItem.Kind == FolderItemKind.Folder && childItem.Left.Exists && childItem.Right.Exists
                    && childItem.Status is FolderCompareStatus.Identical or FolderCompareStatus.Different;
                if (isMixedFolder)
                {
                    foreach (var pair in child.Summary)
                    {
                        Summary[pair.Key] = (Summary.TryGetValue(pair.Key, out var n) ? n : 0) + pair.Value;
                    }
                }
                else
                {
                    var status = ChipStatus(childItem.Status);
                    Summary[status] = (Summary.TryGetValue(status, out var n) ? n : 0) + 1;
                }

                FileCount += childItem.Kind == FolderItemKind.Folder ? child.FileCount : 1;

                // File-level totals: a one-sided folder contributes every file inside it.
                if (childItem.Kind == FolderItemKind.Folder)
                {
                    foreach (var pair in child.Files)
                    {
                        Files[pair.Key] = (Files.TryGetValue(pair.Key, out var n) ? n : 0) + pair.Value;
                    }

                    foreach (var pair in child.Bytes)
                    {
                        Bytes[pair.Key] = (Bytes.TryGetValue(pair.Key, out var n) ? n : 0) + pair.Value;
                    }

                    LeftBytes += child.LeftBytes;
                    RightBytes += child.RightBytes;
                    if (child.Children.Count == 0 && childItem.Status is FolderCompareStatus.Error or FolderCompareStatus.TypeConflict)
                    {
                        Files[FolderCompareStatus.Error] = (Files.TryGetValue(FolderCompareStatus.Error, out var n) ? n : 0) + 1;
                    }
                }
                else
                {
                    var status = ChipStatus(childItem.Status);
                    var left = childItem.Left.Exists ? childItem.Left.Size : 0;
                    var right = childItem.Right.Exists ? childItem.Right.Size : 0;
                    Files[status] = (Files.TryGetValue(status, out var n) ? n : 0) + 1;
                    Bytes[status] = (Bytes.TryGetValue(status, out var b) ? b : 0) + Math.Max(left, right);
                    LeftBytes += left;
                    RightBytes += right;
                }
            }

            if (Depth >= 0)
            {
                Row = new FolderRow(Item, this, Depth, treeMode: true);
            }
        }
    }

    /// <summary>One list row. Text is computed once; only the expansion state changes.</summary>
    private sealed class FolderRow : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public FolderRow(FolderCompareItem item, Node? node, int depth, bool treeMode)
        {
            Item = item;
            Node = node;
            Depth = depth;
            HasChildren = node is { Children.Count: > 0 };
            Indent = new Thickness(depth * IndentPerLevel, 0, 0, 0);

            var slash = item.RelativePath.LastIndexOf('/');
            Folder = treeMode || slash < 0 ? string.Empty : item.RelativePath.Substring(0, slash + 1);
            Name = slash < 0 ? item.RelativePath : item.RelativePath.Substring(slash + 1);
            ExpanderVisibility = treeMode ? Visibility.Visible : Visibility.Collapsed;

            var isFolder = item.Kind == FolderItemKind.Folder;
            (StatusText, StatusBrush) = item.Status switch
            {
                FolderCompareStatus.Different => ("Different", DiffTheme.ChangedBrush),
                FolderCompareStatus.LeftOnly => ("Left only", DiffTheme.RemovedBrush),
                FolderCompareStatus.RightOnly => ("Right only", DiffTheme.AddedBrush),
                FolderCompareStatus.TypeConflict => ("File vs folder", DiffTheme.MovedBrush),
                FolderCompareStatus.Error => ("Unreadable", DiffTheme.MovedBrush),
                FolderCompareStatus.Renamed => ("Renamed", DiffTheme.MovedBrush),
                FolderCompareStatus.Moved => ("Moved", DiffTheme.MovedBrush),
                _ => ("Identical", DiffTheme.IgnoredBrush),
            };
            Details = DescribeDetails(item, node);
            ToolTip = item.Error is null ? item.RelativePath : item.RelativePath + Environment.NewLine + item.Error;
            StatusTint = DiffTheme.Tint(StatusBrush);
            (ImpactWidth, ImpactText) = DescribeImpact(item, node);
            DescribeSize(item, node);
            LeftModified = item.Left.Exists ? FormatDate(item.Left.LastWriteUtc) : string.Empty;
            RightModified = item.Right.Exists ? FormatDate(item.Right.LastWriteUtc) : string.Empty;
            var both = item.Left.Exists && item.Right.Exists && item.Status != FolderCompareStatus.Identical && !isFolder;
            LeftIsNewer = both && item.Left.LastWriteUtc > item.Right.LastWriteUtc;
            RightIsNewer = both && item.Right.LastWriteUtc > item.Left.LastWriteUtc;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public FolderCompareItem Item { get; }

        public Node? Node { get; }

        public int Depth { get; }

        public bool HasChildren { get; }

        public Thickness Indent { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expander)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }

        public string Expander => !HasChildren ? string.Empty : _isExpanded ? "▾" : "▸";

        public Visibility ExpanderVisibility { get; }

        public ImageMoniker Icon => Item.Kind switch
        {
            FolderItemKind.Folder => _isExpanded ? KnownMonikers.FolderOpened : KnownMonikers.FolderClosed,
            FolderItemKind.Link => KnownMonikers.Link,
            _ => KnownMonikers.Document,
        };

        public string Folder { get; }

        public string Name { get; }

        public string StatusText { get; }

        public Brush StatusBrush { get; }

        public string Details { get; }

        public string ToolTip { get; }

        public Brush StatusTint { get; }

        /// <summary>Width of the impact bar, 0 to <see cref="ImpactBarWidth"/>.</summary>
        public double ImpactWidth { get; }

        public string ImpactText { get; }

        public string SizeText { get; private set; } = string.Empty;

        public string SizeDelta { get; private set; } = string.Empty;

        public Brush? SizeDeltaBrush { get; private set; }

        public string LeftModified { get; }

        public string RightModified { get; }

        public bool LeftIsNewer { get; }

        public bool RightIsNewer { get; }

        private static string DescribeDetails(FolderCompareItem item, Node? node)
        {
            if (item.Status == FolderCompareStatus.Error)
            {
                return item.Error ?? "Could not be read";
            }

            if (item.Status is FolderCompareStatus.Renamed or FolderCompareStatus.Moved && item.CounterpartPath is { } counterpart)
            {
                var alike = item.Similarity is double similarity && similarity < 1 ? $"  ({similarity * 100:0}% alike)" : string.Empty;
                return (item.Left.Exists ? "to " : "from ") + counterpart + alike;
            }

            if (item.Kind == FolderItemKind.Folder && node is not null)
            {
                if (item.Status is FolderCompareStatus.LeftOnly or FolderCompareStatus.RightOnly)
                {
                    return node.FileCount == 1 ? "1 file" : $"{node.FileCount:N0} files";
                }

                var parts = new List<string>();
                AddPart(parts, node, FolderCompareStatus.Different, "different");
                AddPart(parts, node, FolderCompareStatus.LeftOnly, "left only");
                AddPart(parts, node, FolderCompareStatus.RightOnly, "right only");
                AddPart(parts, node, FolderCompareStatus.Renamed, "renamed or moved");
                AddPart(parts, node, FolderCompareStatus.Error, "problems");
                return string.Join("  ·  ", parts);
            }

            return item.DifferenceCount is > 0 and var count ? (count == 1 ? "1 change" : $"{count:N0} changes") : string.Empty;
        }

        private static void AddPart(List<string> parts, Node node, FolderCompareStatus status, string label)
        {
            if (node.Summary.TryGetValue(status, out var count) && count > 0)
            {
                parts.Add($"{count:N0} {label}");
            }
        }

        // How much of this entry changed: the share of differing files for a folder on both sides, a
        // log-scaled change count for a file (so 1 change and 200 changes are both readable), full for one-sided.
        private static (double Width, string Text) DescribeImpact(FolderCompareItem item, Node? node)
        {
            if (item.Kind == FolderItemKind.Folder && node is not null && item.Left.Exists && item.Right.Exists)
            {
                var total = node.TotalFiles;
                if (total == 0 || node.ChangedFiles == 0)
                {
                    return (0, string.Empty);
                }

                var share = (double)node.ChangedFiles / total;
                return (Math.Max(2, share * ImpactBarWidth), $"{share * 100:0}%");
            }

            switch (item.Status)
            {
                case FolderCompareStatus.Identical:
                    return (0, string.Empty);
                case FolderCompareStatus.Different when item.DifferenceCount is > 0 and var count:
                    var scaled = Math.Min(1, Math.Log(1 + count) / Math.Log(1 + 200));
                    return (Math.Max(2, scaled * ImpactBarWidth), string.Empty);
                case FolderCompareStatus.Different:
                    return (ImpactBarWidth / 2, string.Empty);
                default:
                    return (ImpactBarWidth, string.Empty);
            }
        }

        // "12.6 KB → 14.6 KB" with a signed delta when both sides exist; the one existing size otherwise.
        private void DescribeSize(FolderCompareItem item, Node? node)
        {
            var isFolder = item.Kind == FolderItemKind.Folder;
            if (isFolder && node is null)
            {
                return;
            }

            var left = isFolder ? node!.LeftBytes : item.Left.Size;
            var right = isFolder ? node!.RightBytes : item.Right.Size;
            if (!item.Left.Exists || !item.Right.Exists)
            {
                SizeText = FormatSize(item.Left.Exists ? left : right);
                return;
            }

            if (left == right)
            {
                SizeText = FormatSize(left);
                return;
            }

            SizeText = $"{FormatSize(left)} → {FormatSize(right)}";
            SizeDelta = (right > left ? "+" : "−") + FormatSize(Math.Abs(right - left));
            SizeDeltaBrush = right > left ? DiffTheme.AddedBrush : DiffTheme.RemovedBrush;
        }

        private static string FormatDate(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
