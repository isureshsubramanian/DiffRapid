using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;
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
using DefGuidList = Microsoft.VisualStudio.Editor.DefGuidList;
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
    private readonly AiPanel _ai;
    private readonly WhitespaceMode _whitespaceWhenIgnored;
    private CompareOptions _options;
    private bool _greyUnimportant;
    private CancellationTokenSource? _compareCancellation;
    private DiffModel? _model;
    private ITextSnapshot? _modelLeftSnapshot;
    private ITextSnapshot? _modelRightSnapshot;
    private int _currentDifference = -1;
    private bool _revealCurrentAfterCompare;
    private bool? _copyAfterCompare;
    private bool _syncingScroll;
    private bool _disposed;

    // The tab "Compare structure" opened, the canonical text it shows, and whether it is still being opened.
    private IVsWindowFrame? _structureFrame;
    private string? _structureKey;
    private bool _structureOpening;

    private TextDiffControl(AsyncPackage package, Pane left, Pane right, CompareOptions options)
    {
        _package = package;
        _left = left;
        _right = right;
        _options = options;

        // The Whitespace toggle switches between exact compare and the configured ignore mode.
        _whitespaceWhenIgnored = options.Whitespace == WhitespaceMode.Compare ? WhitespaceMode.IgnoreChange : options.Whitespace;
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
        _ai = new AiPanel((DiffRapidPackage)package);
        SetDock(_ai, Dock.Bottom);
        Children.Add(_ai);
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

    /// <summary>
    /// What a report calls the left file when the pane shows a temporary copy, e.g. "C:\pkg\A.1.0.nupkg (public API)";
    /// null means the pane's own path.
    /// </summary>
    public string? LeftLabel { get; set; }

    /// <summary>What a report calls the right file; see <see cref="LeftLabel"/>.</summary>
    public string? RightLabel { get; set; }

    /// <summary>
    /// Makes the panes that show text taken from another file (a Word document's text, an assembly's public API)
    /// read-only, so nobody edits a temporary copy thinking it changes the original.
    /// </summary>
    public void MarkExtracted(bool left, bool right)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (left)
        {
            _left.MakeReadOnly();
            _leftHeader.SetReadOnly(LeftLabel ?? _left.Path);
        }

        if (right)
        {
            _right.MakeReadOnly();
            _rightHeader.SetReadOnly(RightLabel ?? _right.Path);
        }
    }

    public static async Task<TextDiffControl> CreateAsync(AsyncPackage package, string leftPath, string rightPath, CompareOptions options)
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
        var control = Diagnostics.Step("Building the comparison view", () => new TextDiffControl(package, left, right, options));
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
        _ai.Close();
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
        var bar = new Grid { Margin = new Thickness(6, 4, 6, 4) };

        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous difference (Shift+F8 or Alt+Up)", () => NavigateDifference(-1)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next difference (F8 or Alt+Down)", () => NavigateDifference(+1)));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Backwards, "Copy to left"), "Replace the current difference on the left with the right side (Ctrl+Shift+F8 or Alt+Left)", () => CopyDifference(toRight: false)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Forwards, "Copy to right"), "Replace the current difference on the right with the left side (Ctrl+F8 or Alt+Right)", () => CopyDifference(toRight: true)));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.SaveAll, "Save"), "Save both files (Ctrl+S)", Save));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.CompareFiles, "Create patch"), "Save the changes from the left file to the right file as a patch (unified diff)", CreatePatch));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Report, "Export report"), "Save the comparison as a side-by-side HTML page or as Markdown for a pull request", ExportReport));
        // A structure comparison is already in canonical form, so it does not offer the button again.
        if (IsStructuredFile(_left.Path) && IsStructuredFile(_right.Path) && !IsStructureView(_left.Path))
        {
            summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.TreeView, "Compare structure"), "Compare the two files with keys, items and attributes in a fixed order, so reordering and layout do not count", CompareStructure));
        }

        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Filter, "Plugins"), "Compare the files after an unpacker or prediffers, such as PrettifyJson or SortLines, have transformed them", CompareWithPlugins));
        summary.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.IntellisenseLightBulb, "Explain"), "Ask the AI service set up in Tools > Options > DiffRapid > AI to explain these differences, risky changes first", Explain));
        summary.Children.Add(DiffTheme.Separator());
        summary.Children.Add(_verdict);
        summary.Children.Add(_metrics);
        bar.Children.Add(summary);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(DiffTheme.Caption("IGNORE"));
        settings.Children.Add(DiffTheme.OptionToggle("Whitespace", "Ignore changes in whitespace", v => UpdateOptions(_options with { Whitespace = v ? _whitespaceWhenIgnored : WhitespaceMode.Compare }), _options.Whitespace != WhitespaceMode.Compare));
        settings.Children.Add(DiffTheme.OptionToggle("Case", "Ignore differences in letter case", v => UpdateOptions(_options with { IgnoreCase = v }), _options.IgnoreCase));
        settings.Children.Add(DiffTheme.OptionToggle("Blank lines", "Ignore inserted or deleted blank lines", v => UpdateOptions(_options with { IgnoreBlankLines = v }), _options.IgnoreBlankLines));
        settings.Children.Add(DiffTheme.OptionToggle("Line endings", "Ignore CRLF / LF differences", v => UpdateOptions(_options with { IgnoreLineEndings = v }), _options.IgnoreLineEndings));
        settings.Children.Add(DiffTheme.OptionToggle(
            "Comments & formatting",
            "Grey out differences that only touch comments; in C# also formatting, #region lines and the order of using directives",
            v =>
            {
                _greyUnimportant = v;
                StartCompare();
            },
            _greyUnimportant));
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
        bar.Children.Add(settings);
        DiffTheme.WrapWhenNarrow(bar, summary, settings);

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

    /// <summary>
    /// Picks how to find differences that do not matter: Roslyn for C# (comments, formatting, regions, using order),
    /// the comment syntax of the file type otherwise; null when the file type has no known comment syntax.
    /// </summary>
    private static Func<TextDiffResult, CancellationToken, IEnumerable<int>>? UnimportantClassifier(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cs" or ".csx")
        {
            return (result, cancellation) => DiffRapid.Semantic.CSharpDiffClassifier.Classify(result, cancellation).Select(h => h.HunkIndex);
        }

        CommentSyntax? syntax = extension switch
        {
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".java" or ".js" or ".ts" or ".tsx" or ".jsx" or ".go" or ".rs" or ".swift" or ".kt" or ".scss" or ".less" or ".css" => CommentSyntax.CStyle,
            ".ps1" or ".psm1" or ".py" or ".sh" or ".rb" or ".yml" or ".yaml" or ".toml" or ".ini" or ".editorconfig" => CommentSyntax.Hash,
            ".sql" => CommentSyntax.Sql,
            ".xml" or ".xaml" or ".config" or ".csproj" or ".vbproj" or ".props" or ".targets" or ".resx" or ".html" or ".htm" or ".svg" => CommentSyntax.Xml,
            _ => null,
        };
        if (syntax is null)
        {
            return null;
        }

        var rules = new ImportanceRules { Comments = syntax };
        return (result, cancellation) => ImportanceClassifier.Classify(result, rules, cancellation).Hunks
            .Where(h => h.Importance == Importance.Unimportant)
            .Select(h => h.HunkIndex);
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
                var unimportant = _greyUnimportant ? UnimportantClassifier(_left.Path) : null;
                var model = await Task.Run(() => DiffModel.Build(leftText, rightText, options, cancellation.Token, unimportant), cancellation.Token);
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

    /// <summary>
    /// Saves a patch that turns the left text into the right text, including unsaved edits. The patch compares
    /// exact content: changes hidden by the ignore options are still changes for whoever applies it.
    /// </summary>
    private void CreatePatch()
    {
        var left = TextDocument.Parse(_left.Buffer.CurrentSnapshot.GetText());
        var right = TextDocument.Parse(_right.Buffer.CurrentSnapshot.GetText());
        var result = TextComparer.Compare(left, right, CompareOptions.Default with { Algorithm = _options.Algorithm });
        if (result.AreEqual)
        {
            _position.Text = "The files are identical: there is nothing to put in a patch";
            return;
        }

        var leftName = System.IO.Path.GetFileName(_left.Path);
        var rightName = System.IO.Path.GetFileName(_right.Path);
        var message = PatchExport.Save(
            System.IO.Path.GetFileNameWithoutExtension(rightName),
            writer => UnifiedDiffWriter.Write(writer, result, "a/" + leftName, "b/" + rightName));
        if (message is not null)
        {
            _position.Text = message;
        }
    }

    /// <summary>Asks the configured AI service to explain the differences as shown, unsaved edits included.</summary>
    private void Explain()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var left = TextDocument.Parse(_left.Buffer.CurrentSnapshot.GetText());
        var right = TextDocument.Parse(_right.Buffer.CurrentSnapshot.GetText());
        var result = TextComparer.Compare(left, right, _options);
        if (result.AreEqual)
        {
            _position.Text = "The files are identical: there is nothing to explain";
            return;
        }

        var leftName = System.IO.Path.GetFileName(LeftLabel ?? _left.Path);
        var rightName = System.IO.Path.GetFileName(RightLabel ?? _right.Path);
        var diff = UnifiedDiffWriter.Write(result, "a/" + leftName, "b/" + rightName);
        var language = System.IO.Path.GetExtension(_right.Path).TrimStart('.');
        _ai.Ask($"What changed: {leftName} → {rightName}", builder => builder.ExplainDiff(diff, leftName, rightName, language.Length == 0 ? null : language));
    }

    /// <summary>
    /// Saves the comparison as a report, including unsaved edits. Unlike a patch, the report follows the ignore
    /// options, so it shows the differences as the view does.
    /// </summary>
    private void ExportReport()
    {
        var left = TextDocument.Parse(_left.Buffer.CurrentSnapshot.GetText());
        var right = TextDocument.Parse(_right.Buffer.CurrentSnapshot.GetText());
        var result = TextComparer.Compare(left, right, _options);
        var leftLabel = LeftLabel ?? _left.Path;
        var rightLabel = RightLabel ?? _right.Path;
        var leftName = System.IO.Path.GetFileName(leftLabel);
        var rightName = System.IO.Path.GetFileName(rightLabel);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export comparison report",
            FileName = string.Concat((leftName + " vs " + rightName + " report")
                .Select(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)),
            Filter = "HTML report, side by side (*.html)|*.html|Markdown for a pull request (*.md)|*.md",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var html = dialog.FilterIndex == 1;
        var options = new FileReportOptions
        {
            LeftLabel = leftLabel,
            RightLabel = rightLabel,
            Title = leftName + " ↔ " + rightName,
            GeneratedAt = DateTimeOffset.Now,
        };

        try
        {
            using (var writer = new StreamWriter(dialog.FileName, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                if (html)
                {
                    FileReportWriter.WriteHtml(result, options, writer);
                }
                else
                {
                    writer.NewLine = "\n";
                    FileReportWriter.WriteMarkdown(result, options, writer);
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

    private const string StructureSuffix = " (structure)";

    private static bool IsStructureView(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(path).EndsWith(StructureSuffix, StringComparison.Ordinal);

    private static bool IsStructuredFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".json" or ".xml" or ".yml" or ".yaml" or ".config" or ".resx"
            or ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj" or ".sqlproj" or ".proj" or ".props" or ".targets" or ".nuspec";

    /// <summary>
    /// Opens a second comparison of both files (with unsaved edits) rewritten in a canonical, key-ordered layout:
    /// moved properties, items and attributes line up, and only real changes remain. Clicking again brings that
    /// tab back while the structure is unchanged, and replaces it after an edit.
    /// </summary>
    /// <summary>Asks for an unpacker and prediffers, then opens what they make of both sides, unsaved edits included.</summary>
    private void CompareWithPlugins()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dialog = new PluginsDialog(System.IO.Path.GetFileName(_left.Path), System.IO.Path.GetFileName(_right.Path));
        if (dialog.ShowModal() != true)
        {
            return;
        }

        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var left = utf8.GetBytes(_left.Buffer.CurrentSnapshot.GetText());
        var right = utf8.GetBytes(_right.Buffer.CurrentSnapshot.GetText());
        var package = (DiffRapidPackage)_package;
        _position.Text = "Running the plugins…";
        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await package.OpenPluginComparisonAsync(_left.Path, left, _right.Path, right, dialog.Options);
                _position.Text = string.Empty;
            }
            catch (StepFailedException ex)
            {
                _position.Text = "The plugins could not run: " + (ex.InnerException ?? ex).Message;
            }
        }).FileAndForget("DiffRapid/CompareWithPlugins");
    }

    private void CompareStructure()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_structureOpening)
        {
            return;
        }

        try
        {
            var leftText = _left.Buffer.CurrentSnapshot.GetText();
            var rightText = _right.Buffer.CurrentSnapshot.GetText();
            var format = DiffRapid.Core.Structured.StructuredReader.DetectFormat(_left.Path, leftText);
            var left = Canonical(leftText, format);
            var right = Canonical(rightText, format);
            var key = left + "\0" + right;
            if (_structureFrame is { } open && IsOpen(open))
            {
                if (key == _structureKey)
                {
                    ErrorHandler.ThrowOnFailure(open.Show());
                    return;
                }

                // Its files are temporary copies, so there is nothing to save.
                open.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }

            _structureFrame = null;
            var extension = format switch
            {
                DiffRapid.Core.Structured.StructuredFormat.Json => ".json",
                DiffRapid.Core.Structured.StructuredFormat.Yaml => ".yaml",
                _ => ".xml",
            };
            var leftTemp = DiffRapid.VisualStudio.Commands.EditorSelection.WriteTemporary(left, System.IO.Path.GetFileName(_left.Path) + StructureSuffix, extension);
            var rightTemp = DiffRapid.VisualStudio.Commands.EditorSelection.WriteTemporary(right, System.IO.Path.GetFileName(_right.Path) + StructureSuffix, extension);
            if (_package is DiffRapidPackage package)
            {
                _structureOpening = true;
                package.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        _structureFrame = await package.OpenTextComparisonAsync(leftTemp, rightTemp);
                        _structureKey = key;
                    }
                    finally
                    {
                        _structureOpening = false;
                    }
                }).FileAndForget("DiffRapid/CompareStructure");
            }
        }
        catch (Exception ex) when (ex is DiffRapid.Core.Structured.StructuredFormatException or ArgumentException)
        {
            _position.Text = "Cannot compare the structure: " + ex.Message;
        }
    }

    // A closed window's frame stays usable as an object but no longer has a document view.
    private static bool IsOpen(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view)) && view is not null;
    }

    private static string Canonical(string text, DiffRapid.Core.Structured.StructuredFormat format) =>
        DiffRapid.Core.Structured.StructuredTextWriter.Write(DiffRapid.Core.Structured.StructuredReader.Read(text, format), format);

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
        if (target.IsReadOnly)
        {
            _position.Text = $"{System.IO.Path.GetFileName(target.Path)} is text taken from the original file and cannot be changed.";
            return;
        }

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

    /// <summary>One side of the comparison: the document buffer and the editor view showing it.</summary>
    private sealed class Pane
    {
        private readonly IVsInvisibleEditor _invisibleEditor;

        private readonly IVsTextView _textView;

        private readonly IVsPersistDocData _docData;

        private readonly IVsTextLines _textLines;

        private readonly ITextDocument? _document;

        private Pane(string path, IVsInvisibleEditor invisibleEditor, IVsTextLines textLines, ITextBuffer buffer, IVsTextView textView, IWpfTextViewHost host)
        {
            Path = path;
            _invisibleEditor = invisibleEditor;
            _docData = (IVsPersistDocData)textLines;
            _textLines = textLines;
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

        /// <summary>True when the pane shows text made from another file (a document's text, an assembly's API) and refuses edits.</summary>
        public bool IsReadOnly { get; private set; }

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

                // A code window tells its views to use the Text Editor fonts and colors; without that, syntax
                // colors fall back to the light-theme defaults whatever the current theme.
                var categories = (IVsTextEditorPropertyCategoryContainer)textView;
                var masterSettings = DefGuidList.guidEditPropCategoryViewMasterSettings;
                ErrorHandler.ThrowOnFailure(categories.GetPropertyCategory(ref masterSettings, out var properties));
                ErrorHandler.ThrowOnFailure(properties.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_FontCategory, DefGuidList.guidTextEditorFontCategory));
                ErrorHandler.ThrowOnFailure(properties.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_ColorCategory, DefGuidList.guidTextEditorFontCategory));

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

        /// <summary>Makes the buffer read-only, for typing and for the comparison's own edits.</summary>
        public void MakeReadOnly()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(_textLines.GetStateFlags(out var flags));
            ErrorHandler.ThrowOnFailure(_textLines.SetStateFlags(flags | (uint)BUFFERSTATEFLAGS.BSF_USER_READONLY));
            IsReadOnly = true;
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
