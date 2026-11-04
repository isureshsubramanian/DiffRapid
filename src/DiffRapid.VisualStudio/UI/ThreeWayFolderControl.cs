using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Core.Folders;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Compares a left and a right folder with the folder both started from, and lists every file by who changed it.
/// Opening a conflict starts a three-way merge saved over the left file; other files compare with the base.
/// </summary>
internal sealed class ThreeWayFolderControl : DockPanel, IComparisonView
{
    private readonly DiffRapidPackage _package;
    private readonly string _baseRoot;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly FolderCompareOptions _options;
    private readonly ListView _list = new();
    private readonly Button _open;
    private readonly ToggleButton _changesOnly;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? _scanCancellation;
    private ThreeWayFolderResult? _result;
    private bool _disposed;

    public ThreeWayFolderControl(DiffRapidPackage package, string baseRoot, string leftRoot, string rightRoot, FolderCompareOptions options)
    {
        _package = package;
        _baseRoot = Path.GetFullPath(baseRoot);
        _leftRoot = Path.GetFullPath(leftRoot);
        _rightRoot = Path.GetFullPath(rightRoot);
        _options = options with { DetectRenames = false };

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        _open = DiffTheme.CommandButton(IconLabel(KnownMonikers.CompareFiles, "Open"), "Merge the selected conflict, or compare the selected file with the base (Enter or double-click)", OpenSelected);
        _open.IsEnabled = false;
        _changesOnly = DiffTheme.OptionToggle("Changes only", "Hide the files that are the same in all three folders", _ => ShowResult(), isChecked: true);

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

        PreviewKeyDown += OnKeyDown;
        StartScan();
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
        _scanCancellation?.Cancel();
    }

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Compare again (F5)", StartScan));
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_open);
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_verdict);
        SetDock(actions, Dock.Left);
        bar.Children.Add(actions);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(_changesOnly);
        SetDock(settings, Dock.Right);
        bar.Children.Add(settings);

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
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var labels = new[]
        {
            PathLabel("LEFT", _leftRoot, DiffTheme.ChangedBrush),
            PathLabel("BASE", _baseRoot, DiffTheme.IgnoredBrush),
            PathLabel("RIGHT", _rightRoot, DiffTheme.ChangedBrush),
        };
        for (var i = 0; i < labels.Length; i++)
        {
            Grid.SetColumn(labels[i], i);
            grid.Children.Add(labels[i]);
        }

        var chrome = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 7, 10, 7) };
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return chrome;
    }

    private static FrameworkElement PathLabel(string badgeText, string path, Brush accent)
    {
        var badge = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = badgeText, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
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
        panel.Children.Add(_details);

        var chrome = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        return chrome;
    }

    private UIElement BuildList()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "File", Width = 380, DisplayMemberBinding = new Binding(nameof(ThreeWayRow.Path)) });
        view.Columns.Add(new GridViewColumn { Header = "Status", Width = 140, CellTemplate = StatusTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Left", Width = 100, DisplayMemberBinding = new Binding(nameof(ThreeWayRow.LeftText)) });
        view.Columns.Add(new GridViewColumn { Header = "Base", Width = 100, DisplayMemberBinding = new Binding(nameof(ThreeWayRow.BaseText)) });
        view.Columns.Add(new GridViewColumn { Header = "Right", Width = 100, DisplayMemberBinding = new Binding(nameof(ThreeWayRow.RightText)) });
        view.Columns.Add(new GridViewColumn { Header = "Opens", Width = 300, DisplayMemberBinding = new Binding(nameof(ThreeWayRow.OpensText)) });

        _list.View = view;
        _list.BorderThickness = new Thickness(0);
        _list.SelectionMode = SelectionMode.Single;
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogListViewStyleKey);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, VsResourceKeys.ThemedDialogListViewItemGridStyleKey);
        _list.Loaded += (_, _) => view.ColumnHeaderContainerStyle = _list.TryFindResource(VsResourceKeys.ThemedDialogGridViewColumnHeaderStyleKey) as Style;
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListViewItem)
            {
                OpenSelected();
            }
        };
        return _list;
    }

    // A soft pill in the status colour, as in the two-way folder comparison.
    private static DataTemplate StatusTemplate()
    {
        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        pill.SetValue(Border.PaddingProperty, new Thickness(8, 1, 8, 1));
        pill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(ThreeWayRow.StatusTint)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ThreeWayRow.StatusText)));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ThreeWayRow.StatusBrush)));
        pill.AppendChild(text);
        return new DataTemplate { VisualTree = pill };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            StartScan();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _list.IsKeyboardFocusWithin)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void StartScan()
    {
        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _details.Text = "Comparing the three folders…";
        _verdict.Text = string.Empty;
        Cursor = Cursors.AppStarting;

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            ThreeWayFolderResult? result = null;
            string? problem = null;
            var started = DateTime.UtcNow;
            try
            {
                result = await Task.Run(() => ThreeWayFolderComparer.Compare(_baseRoot, _leftRoot, _rightRoot, _options, cancellation.Token), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problem = ex.Message;
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed || cancellation.IsCancellationRequested)
            {
                return;
            }

            Cursor = null;
            _result = result;
            ShowResult();
            _details.Text = problem ?? $"Compared in {(DateTime.UtcNow - started).TotalSeconds:0.0} s. Opening a conflict starts a merge that saves over the left file.";
        }).FileAndForget("DiffRapid/ThreeWayFolderCompare");
    }

    private void ShowResult()
    {
        if (_result is not { } result)
        {
            _list.ItemsSource = null;
            _verdict.Text = "Cannot compare the folders";
            _verdict.Foreground = DiffTheme.RemovedBrush;
            UpdateButtons();
            return;
        }

        var selected = (_list.SelectedItem as ThreeWayRow)?.Path;
        var changesOnly = _changesOnly.IsChecked == true;
        var rows = result.Items
            .Where(i => i.Kind != FolderItemKind.Folder && (!changesOnly || i.Status != ThreeWayFolderStatus.Unchanged))
            .Select(i => new ThreeWayRow(i))
            .ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Path == selected) ?? rows.FirstOrDefault();

        var files = result.Items.Where(i => i.Kind != FolderItemKind.Folder).ToList();
        var conflicts = files.Count(i => i.Status == ThreeWayFolderStatus.Conflict);
        var left = files.Count(i => i.Status is ThreeWayFolderStatus.ChangedLeft or ThreeWayFolderStatus.AddedLeft or ThreeWayFolderStatus.RemovedLeft);
        var right = files.Count(i => i.Status is ThreeWayFolderStatus.ChangedRight or ThreeWayFolderStatus.AddedRight or ThreeWayFolderStatus.RemovedRight);
        var parts = new List<string>();
        if (conflicts > 0)
        {
            parts.Add(conflicts == 1 ? "1 conflict" : $"{conflicts:N0} conflicts");
        }

        if (left > 0)
        {
            parts.Add($"{left:N0} changed on the left");
        }

        if (right > 0)
        {
            parts.Add($"{right:N0} changed on the right");
        }

        _verdict.Text = parts.Count == 0 && files.All(i => i.Status == ThreeWayFolderStatus.Unchanged) ? "No changes on either side"
            : parts.Count == 0 ? "Both sides made the same changes"
            : string.Join("  ·  ", parts);
        _verdict.Foreground = conflicts > 0 ? DiffTheme.RemovedBrush : parts.Count > 0 ? DiffTheme.ChangedBrush : DiffTheme.AddedBrush;
        UpdateButtons();
    }

    private void UpdateButtons() =>
        _open.IsEnabled = _list.SelectedItem is ThreeWayRow row && Target(row.Item) is not null;

    private void OpenSelected()
    {
        if (_list.SelectedItem is not ThreeWayRow row || Target(row.Item) is not { } target)
        {
            return;
        }

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (target.Merge)
                {
                    await _package.OpenMergeAsync(target.Left!, target.Base!, target.Right!, target.Left!);
                }
                else if (target.Left is not null && target.Right is not null)
                {
                    await _package.OpenComparisonAsync(target.Left, target.Right);
                }
                else
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.OpenDocument(_package, target.Left ?? target.Right!);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.LogError(ex);
                _details.Text = ex.Message;
            }
        }).FileAndForget("DiffRapid/ThreeWayFolderOpen");
    }

    /// <summary>
    /// What opening an entry does: a merge when both sides changed a file the base also has, otherwise the side
    /// that changed against the base, or the one file that is left.
    /// </summary>
    private OpenTarget? Target(ThreeWayFolderItem item)
    {
        if (item.Kind == FolderItemKind.Folder)
        {
            return null;
        }

        var relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var basePath = item.Base.Exists ? Path.Combine(_baseRoot, relative) : null;
        var left = item.Left.Exists ? Path.Combine(_leftRoot, relative) : null;
        var right = item.Right.Exists ? Path.Combine(_rightRoot, relative) : null;

        return item.Status switch
        {
            ThreeWayFolderStatus.Conflict when basePath is not null && left is not null && right is not null => new OpenTarget(left, basePath, right, Merge: true),
            ThreeWayFolderStatus.Conflict or ThreeWayFolderStatus.AddedBothIdentically when left is not null && right is not null => new OpenTarget(left, null, right, Merge: false),
            ThreeWayFolderStatus.Conflict => Pair(basePath, left ?? right),
            ThreeWayFolderStatus.ChangedLeft or ThreeWayFolderStatus.ChangedBothIdentically => Pair(basePath, left),
            ThreeWayFolderStatus.ChangedRight => Pair(basePath, right),
            _ when (left ?? right ?? basePath) is { } only => new OpenTarget(only, null, null, Merge: false),
            _ => null,
        };
    }

    private static OpenTarget? Pair(string? basePath, string? changed) =>
        basePath is not null && changed is not null ? new OpenTarget(basePath, null, changed, Merge: false)
        : (changed ?? basePath) is { } only ? new OpenTarget(only, null, null, Merge: false)
        : null;

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>The files to open: Left and Right compared (or Left alone), or a merge when <see cref="Merge"/> is set.</summary>
    private sealed class OpenTarget
    {
        public OpenTarget(string? left, string? @base, string? right, bool Merge)
        {
            Left = left;
            Base = @base;
            Right = right;
            this.Merge = Merge;
        }

        public string? Left { get; }

        public string? Base { get; }

        public string? Right { get; }

        public bool Merge { get; }
    }

    /// <summary>One file of the comparison as shown in the list.</summary>
    private sealed class ThreeWayRow
    {
        public ThreeWayRow(ThreeWayFolderItem item)
        {
            Item = item;
        }

        public ThreeWayFolderItem Item { get; }

        public string Path => Item.RelativePath;

        public string LeftText => Describe(Item.Left);

        public string BaseText => Describe(Item.Base);

        public string RightText => Describe(Item.Right);

        public string StatusText => Item.Status switch
        {
            ThreeWayFolderStatus.Unchanged => "Unchanged",
            ThreeWayFolderStatus.ChangedLeft => "Changed left",
            ThreeWayFolderStatus.ChangedRight => "Changed right",
            ThreeWayFolderStatus.ChangedBothIdentically => "Same change",
            ThreeWayFolderStatus.ChangedBoth => "Changed both",
            ThreeWayFolderStatus.Conflict => "Conflict",
            ThreeWayFolderStatus.AddedLeft => "Added left",
            ThreeWayFolderStatus.AddedRight => "Added right",
            ThreeWayFolderStatus.AddedBothIdentically => "Added on both",
            ThreeWayFolderStatus.RemovedLeft => "Removed left",
            ThreeWayFolderStatus.RemovedRight => "Removed right",
            ThreeWayFolderStatus.RemovedBoth => "Removed on both",
            ThreeWayFolderStatus.Skipped => "Skipped",
            _ => "Error",
        };

        public Brush StatusBrush => Item.Status switch
        {
            ThreeWayFolderStatus.Conflict or ThreeWayFolderStatus.Error => DiffTheme.RemovedBrush,
            ThreeWayFolderStatus.ChangedLeft or ThreeWayFolderStatus.ChangedRight or ThreeWayFolderStatus.ChangedBoth => DiffTheme.ChangedBrush,
            ThreeWayFolderStatus.AddedLeft or ThreeWayFolderStatus.AddedRight or ThreeWayFolderStatus.AddedBothIdentically => DiffTheme.AddedBrush,
            ThreeWayFolderStatus.RemovedLeft or ThreeWayFolderStatus.RemovedRight => DiffTheme.MovedBrush,
            _ => DiffTheme.IgnoredBrush,
        };

        public Brush StatusTint => DiffTheme.Tint(StatusBrush);

        public string OpensText => Item.Error ?? Item.Status switch
        {
            ThreeWayFolderStatus.Conflict when Item.Base.Exists && Item.Left.Exists && Item.Right.Exists => "Three-way merge into the left file",
            ThreeWayFolderStatus.Conflict when Item.Left.Exists && Item.Right.Exists => "Left against right (added on both sides)",
            ThreeWayFolderStatus.Conflict => (Item.Left.Exists ? "Removed on the right; " : "Removed on the left; ") + "base against the changed side",
            ThreeWayFolderStatus.ChangedLeft or ThreeWayFolderStatus.ChangedBothIdentically => "Base against left",
            ThreeWayFolderStatus.ChangedRight => "Base against right",
            ThreeWayFolderStatus.AddedBothIdentically => "Left against right",
            _ => string.Empty,
        };

        private static string Describe(FileMeta meta) =>
            !meta.Exists ? "—"
            : meta.Size < 1024 ? $"{meta.Size:N0} B"
            : meta.Size < 1024 * 1024 ? $"{meta.Size / 1024.0:N1} KB"
            : $"{meta.Size / (1024.0 * 1024):N1} MB";
    }
}
