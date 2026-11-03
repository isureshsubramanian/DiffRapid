using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.VisualStudio.Editor;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using OleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using Span = Microsoft.VisualStudio.Text.Span;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Side-by-side text comparison built from two Visual Studio editor views. The views show the live
/// document buffers (including unsaved edits), so syntax highlighting comes from VS itself. Both sides
/// are editable; the comparison is redone shortly after each edit.
/// </summary>
internal sealed class TextDiffControl : DockPanel, IComparisonView, IOleCommandTarget
{
    private static readonly TimeSpan RecompareDelay = TimeSpan.FromMilliseconds(300);

    private readonly AsyncPackage _package;
    private readonly Pane _left;
    private readonly Pane _right;
    private readonly DiffOverviewBar _overview = new();
    private readonly FileHeader _leftHeader;
    private readonly FileHeader _rightHeader;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _changed;
    private readonly TextBlock _added;
    private readonly TextBlock _removed;
    private readonly TextBlock _moved;
    private readonly FrameworkElement _metrics;
    private readonly FrameworkElement _movedMetric;
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DispatcherTimer _recompareTimer;
    private CompareOptions _options = CompareOptions.Default;
    private CancellationTokenSource? _compareCancellation;
    private DiffModel? _model;
    private ITextSnapshot? _modelLeftSnapshot;
    private ITextSnapshot? _modelRightSnapshot;
    private int _currentDifference = -1;
    private bool _revealCurrentAfterCompare;
    private bool? _copyAfterCompare;
    private bool _syncingScroll;
    private bool _disposed;

    private TextDiffControl(AsyncPackage package, Pane left, Pane right)
    {
        _package = package;
        _left = left;
        _right = right;
        _leftHeader = new FileHeader("LEFT", left.Path, DiffTheme.RemovedBrush);
        _rightHeader = new FileHeader("RIGHT", right.Path, DiffTheme.AddedBrush);

        FrameworkElement changed, added, removed, moved;
        (changed, _changed) = DiffTheme.Metric(DiffTheme.ChangedBrush, "Lines changed on both sides");
        (added, _added) = DiffTheme.Metric(DiffTheme.AddedBrush, "Lines only in the right file");
        (removed, _removed) = DiffTheme.Metric(DiffTheme.RemovedBrush, "Lines only in the left file");
        (moved, _moved) = DiffTheme.Metric(DiffTheme.MovedBrush, "Blocks moved to another position");
        var metrics = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        metrics.Children.Add(changed);
        metrics.Children.Add(added);
        metrics.Children.Add(removed);
        metrics.Children.Add(moved);
        _metrics = metrics;
        _movedMetric = moved;

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Focusable = true;

        _recompareTimer = new DispatcherTimer(RecompareDelay, DispatcherPriority.Background, (_, _) => StartCompare(), Dispatcher);
        _recompareTimer.Stop();

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildPanes());

        _left.Buffer.Changed += OnBufferChanged;
        _right.Buffer.Changed += OnBufferChanged;
        _left.DirtyStateChanged += (_, _) => _leftHeader.SetDirty(_left.IsDirty);
        _right.DirtyStateChanged += (_, _) => _rightHeader.SetDirty(_right.IsDirty);
        _leftHeader.SetDirty(_left.IsDirty);
        _rightHeader.SetDirty(_right.IsDirty);
        _left.View.LayoutChanged += (_, _) => SyncScroll(_left, _right);
        _right.View.LayoutChanged += (_, _) => SyncScroll(_right, _left);
        _overview.RowRequested += (_, row) => ScrollToRow(row);
        _left.View.Caret.PositionChanged += (_, e) => SelectDifferenceAt(_left, e.NewPosition.BufferPosition);
        _right.View.Caret.PositionChanged += (_, e) => SelectDifferenceAt(_right, e.NewPosition.BufferPosition);
    }

    public static async Task<TextDiffControl> CreateAsync(AsyncPackage package, string leftPath, string rightPath)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        Diagnostics.Step("Reading " + System.IO.Path.GetFileName(leftPath), () => EnsureText(leftPath));
        Diagnostics.Step("Reading " + System.IO.Path.GetFileName(rightPath), () => EnsureText(rightPath));

        var componentModel = (IComponentModel)(await package.GetServiceAsync(typeof(SComponentModel))
            ?? throw new InvalidOperationException("Component model is unavailable."));
        var invisibleEditors = (IVsInvisibleEditorManager)(await package.GetServiceAsync(typeof(SVsInvisibleEditorManager))
            ?? throw new InvalidOperationException("Invisible editor manager is unavailable."));
        var services = (OleServiceProvider)(await package.GetServiceAsync(typeof(OleServiceProvider))
            ?? throw new InvalidOperationException("The service provider is unavailable."));
        var editorFactory = componentModel.GetService<ITextEditorFactoryService>();
        var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();

        var left = Pane.Open(leftPath, invisibleEditors, adapters, editorFactory, services);
        var right = Pane.Open(rightPath, invisibleEditors, adapters, editorFactory, services);
        var control = Diagnostics.Step("Building the comparison view", () => new TextDiffControl(package, left, right));
        control.StartCompare();
        return control;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recompareTimer.Stop();
        _compareCancellation?.Cancel();
        _left.Buffer.Changed -= OnBufferChanged;
        _right.Buffer.Changed -= OnBufferChanged;
        _left.Close();
        _right.Close();
    }

    private static void EnsureText(string path)
    {
        var buffer = new byte[8000];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }

        // UTF-16 files contain NULs too; only treat as binary when there is no UTF-16/32 byte order mark.
        var hasUnicodeBom = read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!hasUnicodeBom && Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
        {
            throw new NotSupportedException($"'{Path.GetFileName(path)}' looks like a binary file. Binary compare is not available yet.");
        }
    }

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };

        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous difference (Shift+F8 or Alt+Up)", () => NavigateDifference(-1)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next difference (F8 or Alt+Down)", () => NavigateDifference(+1)));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Backwards, "Copy to left"), "Replace the current difference on the left with the right side (Ctrl+Shift+F8 or Alt+Left)", () => CopyDifference(toRight: false)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Forwards, "Copy to right"), "Replace the current difference on the right with the left side (Ctrl+F8 or Alt+Right)", () => CopyDifference(toRight: true)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.SaveAll, "Save"), "Save both files (Ctrl+S)", Save));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_verdict);
        summary.Children.Add(_metrics);
        SetDock(summary, Dock.Left);
        bar.Children.Add(summary);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(DiffTheme.Caption("IGNORE"));
        settings.Children.Add(DiffTheme.OptionToggle("Whitespace", "Ignore changes in whitespace", v => UpdateOptions(_options with { Whitespace = v ? WhitespaceMode.IgnoreChange : WhitespaceMode.Compare })));
        settings.Children.Add(DiffTheme.OptionToggle("Case", "Ignore differences in letter case", v => UpdateOptions(_options with { IgnoreCase = v })));
        settings.Children.Add(DiffTheme.OptionToggle("Blank lines", "Ignore inserted or deleted blank lines", v => UpdateOptions(_options with { IgnoreBlankLines = v })));
        settings.Children.Add(DiffTheme.OptionToggle("Line endings", "Ignore CRLF / LF differences", v => UpdateOptions(_options with { IgnoreLineEndings = v })));
        settings.Children.Add(DiffTheme.Separator());

        settings.Children.Add(DiffTheme.Caption("ALGORITHM"));
        var algorithm = new ComboBox { MinWidth = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How lines are matched between the two files" };
        algorithm.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var kind in (DiffAlgorithmKind[])Enum.GetValues(typeof(DiffAlgorithmKind)))
        {
            algorithm.Items.Add(kind);
        }

        algorithm.SelectedItem = _options.Algorithm;
        algorithm.SelectionChanged += (_, _) => UpdateOptions(_options with { Algorithm = (DiffAlgorithmKind)algorithm.SelectedItem });
        settings.Children.Add(algorithm);
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Compare again", StartCompare));
        SetDock(settings, Dock.Right);
        bar.Children.Add(settings);

        var chrome = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        chrome.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        chrome.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarToolBarBorderBrushKey);
        chrome.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        chrome.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.CommandBarGradientBeginColorKey);
        return chrome;
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

    private UIElement BuildPanes()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddCell(grid, _leftHeader, 0, 0);
        AddCell(grid, _rightHeader, 0, 2);
        AddCell(grid, _left.Host.HostControl, 1, 0);
        AddCell(grid, _right.Host.HostControl, 1, 2);
        AddCell(grid, _overview, 1, 3);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to resize" };
        splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        Grid.SetRowSpan(splitter, 2);
        AddCell(grid, splitter, 0, 1);
        return grid;
    }

    private static void AddCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void UpdateOptions(CompareOptions options)
    {
        _options = options;
        StartCompare();
    }

    private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        _recompareTimer.Stop();
        _recompareTimer.Start();
    }

    private void StartCompare()
    {
        _recompareTimer.Stop();
        if (_disposed)
        {
            return;
        }

        _compareCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _compareCancellation = cancellation;

        var leftSnapshot = _left.Buffer.CurrentSnapshot;
        var rightSnapshot = _right.Buffer.CurrentSnapshot;
        var leftText = leftSnapshot.GetText();
        var rightText = rightSnapshot.GetText();
        var options = _options;
        _verdict.Text = "Comparing…";
        _verdict.ClearValue(TextBlock.ForegroundProperty);
        _metrics.Visibility = Visibility.Collapsed;

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var model = await Task.Run(() => DiffModel.Build(leftText, rightText, options, cancellation.Token), cancellation.Token);
                var elapsed = stopwatch.Elapsed;
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    _modelLeftSnapshot = leftSnapshot;
                    _modelRightSnapshot = rightSnapshot;
                    Apply(model, options, elapsed);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _verdict.Text = "Compare failed";
                _verdict.Foreground = DiffTheme.RemovedBrush;
                _position.Text = ex.Message;
            }
        }).FileAndForget("DiffRapid/Compare");
    }

    private void Apply(DiffModel model, CompareOptions options, TimeSpan elapsed)
    {
        _model = model;

        // Keep the position across re-compares; after a copy the same index is the next difference.
        _currentDifference = Math.Min(_currentDifference, model.HunkRows.Count - 1);
        DiffPaneState.Set(_left.View, model.Left);
        DiffPaneState.Set(_right.View, model.Right);
        Relayout(_left.View);
        Relayout(_right.View);
        SyncScroll(_left, _right);
        _overview.SetModel(model);

        int changed = 0, added = 0, removed = 0;
        foreach (var row in model.Rows)
        {
            switch (row.Kind)
            {
                case AlignedRowKind.Changed:
                    changed++;
                    break;
                case AlignedRowKind.Added:
                    added++;
                    break;
                case AlignedRowKind.Removed:
                    removed++;
                    break;
            }
        }

        var count = model.HunkRows.Count;
        var moved = model.Result.MovedBlocks.Count;
        if (count == 0)
        {
            _verdict.Text = "✔  Files are identical";
            _verdict.Foreground = DiffTheme.AddedBrush;
            _metrics.Visibility = Visibility.Collapsed;
        }
        else
        {
            _verdict.Text = Plural(count, "difference");
            _verdict.ClearValue(TextBlock.ForegroundProperty);
            _changed.Text = $"{changed:N0} changed";
            _added.Text = $"{added:N0} added";
            _removed.Text = $"{removed:N0} removed";
            _moved.Text = $"{moved:N0} moved";
            _movedMetric.Visibility = moved > 0 ? Visibility.Visible : Visibility.Collapsed;
            _metrics.Visibility = Visibility.Visible;
        }

        _leftHeader.SetLineCount(_left.Buffer.CurrentSnapshot.LineCount);
        _rightHeader.SetLineCount(_right.Buffer.CurrentSnapshot.LineCount);
        UpdatePosition();
        _details.Text = $"{options.Algorithm} algorithm  ·  {model.Rows.Count:N0} rows  ·  compared in {FormatElapsed(elapsed)}";

        if (_revealCurrentAfterCompare)
        {
            _revealCurrentAfterCompare = false;
            if (_currentDifference >= 0)
            {
                ScrollToRow(model.HunkRows[_currentDifference]);
            }
        }

        if (_copyAfterCompare is { } toRight)
        {
            _copyAfterCompare = null;
            CopyDifference(toRight);
        }
    }

    /// <summary>Replaces the current difference on one side with the lines from the other side.</summary>
    private void CopyDifference(bool toRight)
    {
        var model = _model;
        if (model is null || model.HunkRows.Count == 0)
        {
            return;
        }

        if (_currentDifference < 0)
        {
            _position.Text = "Click inside a difference or press F8 to pick one, then copy it.";
            return;
        }

        // The model must describe the buffers as they are now; otherwise copy once the re-compare is done.
        if (_left.Buffer.CurrentSnapshot != _modelLeftSnapshot || _right.Buffer.CurrentSnapshot != _modelRightSnapshot)
        {
            _copyAfterCompare = toRight;
            StartCompare();
            return;
        }

        var hunk = model.HunkAt(_currentDifference);
        var (source, sourceStart, sourceCount) = toRight ? (_modelLeftSnapshot!, hunk.LeftStart, hunk.LeftCount) : (_modelRightSnapshot!, hunk.RightStart, hunk.RightCount);
        var (target, targetStart, targetCount) = toRight ? (_right, hunk.RightStart, hunk.RightCount) : (_left, hunk.LeftStart, hunk.LeftCount);

        var targetSnapshot = target.Buffer.CurrentSnapshot;
        var replace = LinesSpan(targetSnapshot, targetStart, targetCount);
        var text = source.GetText(LinesSpan(source, sourceStart, sourceCount));

        if (text.Length > 0)
        {
            var lineBreak = LineBreakOf(targetSnapshot);

            // Copied lines that ended the source file need a break when they land before more text.
            if (!EndsWithLineBreak(text) && replace.End < targetSnapshot.Length)
            {
                text += lineBreak;
            }

            // Appending after a last line that has no break: start a new line first.
            if (replace.Start == targetSnapshot.Length && replace.Start > 0 && !EndsWithLineBreak(targetSnapshot.GetText(replace.Start - 1, 1)))
            {
                text = lineBreak + text;
            }
        }

        try
        {
            using var edit = target.Buffer.CreateEdit();
            edit.Replace(replace, text);
            edit.Apply();
            _revealCurrentAfterCompare = true;
        }
        catch (InvalidOperationException ex)
        {
            _position.Text = $"Could not change {System.IO.Path.GetFileName(target.Path)}: {ex.Message}";
        }
    }

    private static Span LinesSpan(ITextSnapshot snapshot, int startLine, int lineCount)
    {
        var start = startLine < snapshot.LineCount ? snapshot.GetLineFromLineNumber(startLine).Start.Position : snapshot.Length;
        var endLine = startLine + lineCount;
        var end = endLine < snapshot.LineCount ? snapshot.GetLineFromLineNumber(endLine).Start.Position : snapshot.Length;
        return Span.FromBounds(start, Math.Max(start, end));
    }

    private static bool EndsWithLineBreak(string text) => text.Length > 0 && text[text.Length - 1] is '\n' or '\r';

    private static string LineBreakOf(ITextSnapshot snapshot)
    {
        foreach (var line in snapshot.Lines)
        {
            var lineBreak = line.GetLineBreakText();
            if (lineBreak.Length > 0)
            {
                return lineBreak;
            }
        }

        return Environment.NewLine;
    }

    public bool IsDirty => _left.IsDirty || _right.IsDirty;

    // The window's commands (typing, undo, cut/copy/paste...) go to the editor that has focus.
    private Pane? FocusedPane => _left.HasFocus ? _left : _right.HasFocus ? _right : null;

    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (cCmds == 1 && OwnCommand(pguidCmdGroup, prgCmds[0].cmdID) is not null)
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }

        return FocusedPane is { } pane
            ? pane.CommandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
            : (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (OwnCommand(pguidCmdGroup, nCmdID) is { } command)
        {
            command();
            return VSConstants.S_OK;
        }

        return FocusedPane is { } pane
            ? pane.CommandTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
            : (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    // Commands the comparison handles itself rather than the editors: stepping and copying differences
    // (F8 keys and Alt+arrows in the DiffRapid key binding scope) and saving, which saves both files.
    private Action? OwnCommand(Guid group, uint id)
    {
        if (group == PackageIds.CommandSet)
        {
            return (int)id switch
            {
                PackageIds.PreviousDifference => () => NavigateDifference(-1),
                PackageIds.NextDifference => () => NavigateDifference(+1),
                PackageIds.CopyToLeft => () => CopyDifference(toRight: false),
                PackageIds.CopyToRight => () => CopyDifference(toRight: true),
                _ => null,
            };
        }

        if (group == VSConstants.GUID_VSStandardCommandSet97
            && (VSConstants.VSStd97CmdID)id is VSConstants.VSStd97CmdID.Save or VSConstants.VSStd97CmdID.SaveProjectItem)
        {
            return Save;
        }

        return null;
    }

    /// <summary>Makes the difference under the caret the current one, so copying acts on what was clicked.</summary>
    private void SelectDifferenceAt(Pane pane, SnapshotPoint position)
    {
        var model = _model;
        var state = DiffPaneState.Get(pane.View);
        if (model is null || state is null)
        {
            return;
        }

        var line = position.GetContainingLine().LineNumber;
        if (line >= state.LineCount)
        {
            return;
        }

        var hunk = model.Rows[state.RowOfLine[line]].HunkIndex;
        if (hunk < 0)
        {
            return;
        }

        for (var i = 0; i < model.HunkRows.Count; i++)
        {
            if (model.Rows[model.HunkRows[i]].HunkIndex == hunk)
            {
                if (i != _currentDifference)
                {
                    _currentDifference = i;
                    UpdatePosition();
                }

                return;
            }
        }
    }

    /// <summary>Saves whichever of the two files has unsaved changes.</summary>
    public void Save()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var pane in new[] { _left, _right })
        {
            try
            {
                pane.Save();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _position.Text = $"Could not save {System.IO.Path.GetFileName(pane.Path)}: {ex.Message}";
                return;
            }
        }
    }

    private void UpdatePosition()
    {
        var count = _model?.HunkRows.Count ?? 0;
        _position.Text = count == 0
            ? "No differences"
            : _currentDifference < 0
                ? $"{Plural(count, "difference")}  ·  press F8 or Alt+Down to step through them"
                : $"Difference {_currentDifference + 1} of {count:N0}";
    }

    private static string Plural(int count, string singular) => $"{count:N0} {(count == 1 ? singular : singular + "s")}";

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000 ? $"{Math.Max(1, (int)elapsed.TotalMilliseconds)} ms" : $"{elapsed.TotalSeconds:0.0} s";

    // Forces the editor to re-query line transforms and repaint adornments.
    private static void Relayout(IWpfTextView view)
    {
        if (view.IsClosed || view.InLayout || view.TextViewLines is null)
        {
            return;
        }

        var first = view.TextViewLines.FirstVisibleLine;
        view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - view.ViewportTop, ViewRelativePosition.Top);
    }

    private void SyncScroll(Pane source, Pane target)
    {
        if (_syncingScroll || _model is null || source.View.InLayout || target.View.InLayout
            || source.View.TextViewLines is null || target.View.TextViewLines is null)
        {
            return;
        }

        var sourceState = DiffPaneState.Get(source.View);
        var targetState = DiffPaneState.Get(target.View);
        if (sourceState is null || targetState is null || sourceState.LineCount == 0 || targetState.LineCount == 0)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            var height = source.View.LineHeight;
            var first = source.View.TextViewLines.FirstVisibleLine;
            var line = Math.Min(first.Start.GetContainingLine().LineNumber, sourceState.LineCount - 1);
            var topRow = sourceState.BlockTopRow(line) + ((source.View.ViewportTop - first.Top) / height);

            var targetLine = targetState.LineAtOrAfterRow((int)Math.Floor(topRow));
            var offset = (targetState.BlockTopRow(targetLine) - topRow) * height;
            var snapshotLine = target.View.TextSnapshot.GetLineFromLineNumber(Math.Min(targetLine, target.View.TextSnapshot.LineCount - 1));
            target.View.DisplayTextLineContainingBufferPosition(snapshotLine.Start, offset, ViewRelativePosition.Top);
            target.View.ViewportLeft = source.View.ViewportLeft;

            var leftPane = source == _left ? source : target;
            var leftState = DiffPaneState.Get(leftPane.View)!;
            var leftFirst = leftPane.View.TextViewLines.FirstVisibleLine;
            var leftLine = Math.Min(leftFirst.Start.GetContainingLine().LineNumber, leftState.LineCount - 1);
            var leftTopRow = leftState.BlockTopRow(leftLine) + ((leftPane.View.ViewportTop - leftFirst.Top) / height);
            _overview.SetViewport(leftTopRow, leftPane.View.ViewportHeight / height);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void ScrollToRow(int row)
    {
        var state = DiffPaneState.Get(_left.View);
        if (state is null || state.LineCount == 0)
        {
            return;
        }

        var line = state.LineAtOrAfterRow(row);
        var snapshotLine = _left.View.TextSnapshot.GetLineFromLineNumber(Math.Min(line, _left.View.TextSnapshot.LineCount - 1));
        var offset = (_left.View.ViewportHeight / 3) - ((state.RowOfLine[line] - row) * _left.View.LineHeight);
        _left.View.DisplayTextLineContainingBufferPosition(snapshotLine.Start, Math.Max(0, offset), ViewRelativePosition.Top);
        SyncScroll(_left, _right);
    }

    private void NavigateDifference(int direction)
    {
        var model = _model;
        var state = DiffPaneState.Get(_left.View);
        if (model is null || state is null || model.HunkRows.Count == 0 || state.LineCount == 0)
        {
            return;
        }

        var first = _left.View.TextViewLines.FirstVisibleLine;
        var line = Math.Min(first.Start.GetContainingLine().LineNumber, state.LineCount - 1);
        var anchorRow = state.BlockTopRow(line) + (int)(_left.View.ViewportHeight / 3 / _left.View.LineHeight);

        var target = -1;
        if (direction > 0)
        {
            for (var i = 0; i < model.HunkRows.Count; i++)
            {
                if (model.HunkRows[i] > anchorRow)
                {
                    target = i;
                    break;
                }
            }
        }
        else
        {
            for (var i = model.HunkRows.Count - 1; i >= 0; i--)
            {
                if (model.HunkRows[i] < anchorRow)
                {
                    target = i;
                    break;
                }
            }
        }

        if (target >= 0)
        {
            _currentDifference = target;
            UpdatePosition();
            ScrollToRow(model.HunkRows[target]);
        }
    }

    /// <summary>Title block above each editor: side badge, file name, folder and line count.</summary>
    private sealed class FileHeader : Border
    {
        private readonly TextBlock _lines = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock _modified = new()
        {
            Text = "● Unsaved",
            Foreground = DiffTheme.ChangedBrush,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "This file has changes that are not saved yet (Ctrl+S saves both files)",
        };

        public FileHeader(string side, string path, Brush accent)
        {
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(10, 7, 10, 7);
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            ToolTip = path;

            var badge = new Border
            {
                Background = accent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = side, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            };

            var name = new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var folder = new TextBlock
            {
                Text = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            folder.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
            _lines.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);

            var row = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(_lines, Dock.Right);
            DockPanel.SetDock(_modified, Dock.Right);
            row.Children.Add(badge);
            row.Children.Add(name);
            row.Children.Add(_lines);
            row.Children.Add(_modified);
            row.Children.Add(folder);
            Child = row;
        }

        public void SetLineCount(int lines) => _lines.Text = lines == 1 ? "1 line" : $"{lines:N0} lines";

        public void SetDirty(bool dirty) => _modified.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One side of the comparison: the document buffer and the editor view showing it.</summary>
    private sealed class Pane
    {
        private readonly IVsInvisibleEditor _invisibleEditor;

        private readonly IVsTextView _textView;

        private readonly IVsPersistDocData _docData;

        private readonly ITextDocument? _document;

        private Pane(string path, IVsInvisibleEditor invisibleEditor, IVsTextLines textLines, ITextBuffer buffer, IVsTextView textView, IWpfTextViewHost host)
        {
            Path = path;
            _invisibleEditor = invisibleEditor;
            _docData = (IVsPersistDocData)textLines;
            _textView = textView;
            Buffer = buffer;
            Host = host;
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            {
                _document = document;
                _document.DirtyStateChanged += (s, e) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? DirtyStateChanged;

        public string Path { get; }

        public bool IsDirty => _document?.IsDirty ?? false;

        public ITextBuffer Buffer { get; }

        public IWpfTextViewHost Host { get; }

        public IWpfTextView View => Host.TextView;

        /// <summary>The editor's command chain: typing, undo, clipboard and the other editing commands.</summary>
        public IOleCommandTarget CommandTarget => (IOleCommandTarget)_textView;

        public bool HasFocus => Host.HostControl.IsKeyboardFocusWithin;

        public static Pane Open(
            string path,
            IVsInvisibleEditorManager invisibleEditors,
            IVsEditorAdaptersFactoryService adapters,
            ITextEditorFactoryService editorFactory,
            OleServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var name = System.IO.Path.GetFileName(path);
            var invisibleEditor = Diagnostics.Step($"Opening {name} in the editor", () =>
            {
                ErrorHandler.ThrowOnFailure(invisibleEditors.RegisterInvisibleEditor(
                    path, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out var editor));
                return editor;
            });

            var (textLines, buffer) = Diagnostics.Step($"Getting the text buffer of {name}", () =>
            {
                var iid = typeof(IVsTextLines).GUID;
                ErrorHandler.ThrowOnFailure(invisibleEditor.GetDocData(0, ref iid, out var docDataPtr));
                try
                {
                    var lines = (IVsTextLines)Marshal.GetObjectForIUnknown(docDataPtr);
                    return (lines, adapters.GetDocumentBuffer(lines)
                        ?? throw new InvalidOperationException("The document has no text buffer."));
                }
                finally
                {
                    Marshal.Release(docDataPtr);
                }
            });

            // A view made through the adapters gets Visual Studio's editing command chain (typing, undo,
            // clipboard, IntelliSense); the pane forwards the window's commands to it.
            var (vsView, host) = Diagnostics.Step($"Creating the editor view for {name}", () =>
            {
                var roles = editorFactory.CreateTextViewRoleSet(
                    PredefinedTextViewRoles.Document,
                    PredefinedTextViewRoles.Analyzable,
                    PredefinedTextViewRoles.Editable,
                    PredefinedTextViewRoles.Interactive,
                    PredefinedTextViewRoles.Zoomable,
                    DiffRapidRoles.Pane);
                var textView = adapters.CreateVsTextViewAdapter(services, roles);
                var init = new[] { new INITVIEW { fSelectionMargin = 0, fWidgetMargin = 0, fVirtualSpace = 0, fDragDropMove = 1 } };
                const uint flags = (uint)TextViewInitFlags.VIF_HSCROLL | (uint)TextViewInitFlags.VIF_VSCROLL | (uint)TextViewInitFlags3.VIF_NO_HWND_SUPPORT;
                ErrorHandler.ThrowOnFailure(textView.Initialize(textLines, IntPtr.Zero, flags, init));

                var viewHost = adapters.GetWpfTextViewHost(textView)
                    ?? throw new InvalidOperationException("The editor view has no WPF host.");
                var view = viewHost.TextView;
                view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
                view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
                view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
                view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
                return (textView, viewHost);
            });

            return new Pane(path, invisibleEditor, textLines, buffer, vsView, host);
        }

        /// <summary>
        /// Saves through the document's own data, as its owner would; going through the running document table
        /// instead asks the file's owner, which for a file outside any project is the solution.
        /// </summary>
        public void Save()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsDirty)
            {
                return;
            }

            ErrorHandler.ThrowOnFailure(_docData.SaveDocData(VSSAVEFLAGS.VSSAVE_Save, out _, out _));
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Host.IsClosed)
            {
                _textView.CloseView();
            }

            GC.KeepAlive(_invisibleEditor);
        }
    }
}
