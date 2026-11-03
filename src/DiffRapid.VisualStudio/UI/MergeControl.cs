using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Merge;
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
using Microsoft.VisualStudio.Utilities;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using DefGuidList = Microsoft.VisualStudio.Editor.DefGuidList;
using OleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using Span = Microsoft.VisualStudio.Text.Span;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Three-way merge: the left, base and right versions side by side, aligned row by row, above the merged
/// result. Changes made on one side only are taken automatically; each conflict is resolved by choosing a
/// side (or both), or by editing the result directly, and the result is saved to the chosen file.
/// </summary>
internal sealed class MergeControl : DockPanel, IComparisonView, IOleCommandTarget
{
    private readonly MergeLayout _layout;
    private readonly ConflictLabels _labels;
    private readonly string _resultPath;
    private readonly string? _overwrittenInput;
    private readonly DecodedText _leftFile;
    private readonly Pane _left;
    private readonly Pane _base;
    private readonly Pane _right;
    private readonly Pane _result;
    private readonly FileHeader _resultHeader;
    private readonly int _conflictCount;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

    // Where each chunk starts in the result; chunk i runs to the start of chunk i + 1. Text typed at a
    // boundary belongs to the chunk that starts there.
    private ITrackingPoint[] _chunkStarts = Array.Empty<ITrackingPoint>();
    private IReadOnlyList<(int Start, int Count)> _ranges = Array.Empty<(int, int)>();
    private string? _savedText;
    private int _currentChunk = -1;
    private bool _applyingEdit;
    private bool _overwriteConfirmed;
    private bool _syncingScroll;
    private bool _disposed;

    private MergeControl(
        MergeLayout layout,
        string leftPath,
        string basePath,
        string rightPath,
        string resultPath,
        DecodedText leftFile,
        string? savedText,
        Pane left,
        Pane @base,
        Pane right,
        Pane result)
    {
        _layout = layout;
        _resultPath = resultPath;
        _overwrittenInput = new[] { (leftPath, "left"), (basePath, "base"), (rightPath, "right") }
            .Where(input => string.Equals(Path.GetFullPath(input.Item1), Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
            .Select(input => input.Item2)
            .FirstOrDefault();
        _leftFile = leftFile;
        _savedText = savedText;
        _labels = Labels(leftPath, basePath, rightPath);
        _conflictCount = layout.Result.Chunks.Count(c => c.Kind == MergeChunkKind.Conflict);
        _left = left;
        _base = @base;
        _right = right;
        _result = result;
        DiffPaneState.Set(_left.View, layout.Left);
        DiffPaneState.Set(_base.View, layout.Base);
        DiffPaneState.Set(_right.View, layout.Right);

        var leftHeader = new FileHeader("LEFT", leftPath, DiffTheme.MovedBrush);
        var baseHeader = new FileHeader("BASE", basePath, DiffTheme.IgnoredBrush);
        var rightHeader = new FileHeader("RIGHT", rightPath, DiffTheme.MovedBrush);
        _resultHeader = new FileHeader(_overwrittenInput is null ? "RESULT" : "RESULT = " + _overwrittenInput.ToUpperInvariant(), resultPath, DiffTheme.AddedBrush);
        leftHeader.SetLineCount(layout.Left.LineCount);
        baseHeader.SetLineCount(layout.Base.LineCount);
        rightHeader.SetLineCount(layout.Right.LineCount);

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Focusable = true;

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var footer = BuildFooter();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(BuildPanes(leftHeader, baseHeader, rightHeader));

        foreach (var source in new[] { _left, _base, _right })
        {
            source.View.LayoutChanged += (_, _) => SyncScroll(source.View);
            source.View.Caret.PositionChanged += (_, e) => SelectChunkAt(source.View, e.NewPosition.BufferPosition);
        }

        _result.View.Caret.PositionChanged += (_, e) => SelectChunkInResult(e.NewPosition.BufferPosition);
        _result.Buffer.Changed += OnResultEdited;

        var snapshot = _result.Buffer.CurrentSnapshot;
        _chunkStarts = layout.Session.OutputLineRanges(_labels)
            .Select(range => snapshot.CreateTrackingPoint(LinePosition(snapshot, range.Start), PointTrackingMode.Negative))
            .ToArray();
        UpdateView();

        _details.Text = $"{Plural(_conflictCount, "conflict")}  ·  {Plural(layout.Result.Chunks.Count(c => c.Kind is MergeChunkKind.LeftOnly or MergeChunkKind.RightOnly or MergeChunkKind.BothSame), "change")} merged automatically  ·  the result can be edited";
        Loaded += OnFirstLoaded;
    }

    public static async Task<MergeControl> CreateAsync(AsyncPackage package, string leftPath, string basePath, string rightPath, string resultPath, CompareOptions options)
    {
        var (left, @base, right, saved) = await Task.Run(() =>
        {
            var l = ReadText(leftPath);
            var b = ReadText(basePath);
            var r = ReadText(rightPath);
            var s = File.Exists(resultPath) ? TextFileReader.ReadFile(resultPath).Text : null;
            return (l, b, r, s);
        });

        var layout = await Task.Run(() => MergeLayout.Build(@base.Text, left.Text, right.Text, options, package.DisposalToken));

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var componentModel = (IComponentModel)(await package.GetServiceAsync(typeof(SComponentModel))
            ?? throw new InvalidOperationException("Component model is unavailable."));
        var services = (OleServiceProvider)(await package.GetServiceAsync(typeof(OleServiceProvider))
            ?? throw new InvalidOperationException("The service provider is unavailable."));
        var contentType = ContentTypeFor(resultPath, componentModel);

        return Diagnostics.Step("Building the merge view", () =>
        {
            Pane Create(string text, bool readOnly) => Pane.Create(text, readOnly, contentType, componentModel, services);
            return new MergeControl(
                layout, leftPath, basePath, rightPath, resultPath, left, saved,
                Create(left.Text, readOnly: true),
                Create(@base.Text, readOnly: true),
                Create(right.Text, readOnly: true),
                // The result starts as the automatic merge, with conflict markers where a choice is needed.
                Create(layout.Session.ToText(Labels(leftPath, basePath, rightPath)), readOnly: false));
        });
    }

    private static ConflictLabels Labels(string leftPath, string basePath, string rightPath) =>
        new(Path.GetFileName(leftPath), Path.GetFileName(basePath), Path.GetFileName(rightPath));

    private static DecodedText ReadText(string path)
    {
        var text = TextFileReader.ReadFile(path);
        if (text.IsBinary)
        {
            throw new NotSupportedException($"'{Path.GetFileName(path)}' looks like a binary file. Only text files can be merged.");
        }

        return text;
    }

    private static IContentType ContentTypeFor(string path, IComponentModel componentModel)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        var byExtension = extension.Length == 0
            ? null
            : componentModel.GetService<IFileExtensionRegistryService>().GetContentTypeForExtension(extension);
        return byExtension is null || byExtension.TypeName == "UNKNOWN"
            ? componentModel.GetService<IContentTypeRegistryService>().GetContentType("text")
            : byExtension;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _result.Buffer.Changed -= OnResultEdited;
        foreach (var pane in new[] { _left, _base, _right, _result })
        {
            pane.Close();
        }
    }

    public bool IsDirty => !string.Equals(_result.Buffer.CurrentSnapshot.GetText(), _savedText, StringComparison.Ordinal);

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        if (_conflictCount > 0)
        {
            Dispatcher.BeginInvoke(new Action(() => GoToConflict(+1, unresolvedOnly: true)), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };

        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous conflict (Shift+F8)", () => GoToConflict(-1, unresolvedOnly: false)));
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next conflict (F8)", () => GoToConflict(+1, unresolvedOnly: false)));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(DiffTheme.Caption("TAKE"));
        commands.Children.Add(DiffTheme.CommandButton(Label("Left"), "Resolve the current conflict with the left version (Ctrl+Shift+F8)", () => ResolveCurrent(MergeResolution.Left)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Base"), "Resolve the current conflict by keeping the base version (neither change)", () => ResolveCurrent(MergeResolution.Base)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Right"), "Resolve the current conflict with the right version (Ctrl+F8)", () => ResolveCurrent(MergeResolution.Right)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Left + right"), "Resolve the current conflict with the left lines followed by the right lines", () => ResolveCurrent(MergeResolution.LeftThenRight)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Right + left"), "Resolve the current conflict with the right lines followed by the left lines", () => ResolveCurrent(MergeResolution.RightThenLeft)));
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Undo, "Reset"), "Put the conflict markers back for the current conflict, discarding its edits", () => ResolveCurrent(MergeResolution.Unresolved)));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Save, "Save"), "Save the merged result (Ctrl+S)", Save));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(_verdict);
        SetDock(commands, Dock.Left);
        bar.Children.Add(commands);

        var all = new StackPanel { Orientation = Orientation.Horizontal };
        all.Children.Add(DiffTheme.Caption("ALL CONFLICTS"));
        all.Children.Add(DiffTheme.CommandButton(Label("Left"), "Resolve every conflict with the left version", () => ResolveAll(MergeResolution.Left)));
        all.Children.Add(DiffTheme.CommandButton(Label("Right"), "Resolve every conflict with the right version", () => ResolveAll(MergeResolution.Right)));
        all.Children.Add(DiffTheme.CommandButton(Label("Reset"), "Put the conflict markers back for every conflict, discarding their edits", () => ResolveAll(MergeResolution.Unresolved)));
        SetDock(all, Dock.Right);
        bar.Children.Add(all);

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

    private UIElement BuildPanes(FileHeader leftHeader, FileHeader baseHeader, FileHeader rightHeader)
    {
        var sources = new Grid();
        sources.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sources.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var column = 0;
        foreach (var (header, pane) in new (FileHeader, Pane)[] { (leftHeader, _left), (baseHeader, _base), (rightHeader, _right) })
        {
            if (column > 0)
            {
                sources.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to resize" };
                splitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                Grid.SetRowSpan(splitter, 2);
                AddCell(sources, splitter, 0, column++);
            }

            sources.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddCell(sources, header, 0, column);
            AddCell(sources, pane.Host.HostControl, 1, column++);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        AddCell(grid, sources, 0, 0);
        var resultSplitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Rows, ToolTip = "Drag to resize" };
        resultSplitter.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        AddCell(grid, resultSplitter, 1, 0);
        AddCell(grid, _resultHeader, 2, 0);
        AddCell(grid, _result.Host.HostControl, 3, 0);
        return grid;
    }

    private static void AddCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new CrispImage { Moniker = moniker, Width = 16, Height = 16 });
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static int LinePosition(ITextSnapshot snapshot, int line) =>
        line < snapshot.LineCount ? snapshot.GetLineFromLineNumber(line).Start.Position : snapshot.Length;

    /// <summary>Start and end of every chunk in the current result text.</summary>
    private (int Start, int End)[] ChunkSpans(ITextSnapshot snapshot)
    {
        var spans = new (int, int)[_chunkStarts.Length];
        for (var i = 0; i < spans.Length; i++)
        {
            var start = _chunkStarts[i].GetPosition(snapshot);
            var end = i + 1 < spans.Length ? _chunkStarts[i + 1].GetPosition(snapshot) : snapshot.Length;
            spans[i] = (start, Math.Max(start, end));
        }

        return spans;
    }

    /// <summary>Text of each chunk as the session renders it for the current choices.</summary>
    private string[] RenderedChunks()
    {
        var session = _layout.Session;
        var lines = TextDocument.Parse(session.ToText(_labels)).Lines;
        var ranges = session.OutputLineRanges(_labels);
        var texts = new string[ranges.Count];
        var sb = new StringBuilder();
        for (var i = 0; i < ranges.Count; i++)
        {
            sb.Clear();
            for (var line = ranges[i].Start; line < ranges[i].Start + ranges[i].Count && line < lines.Count; line++)
            {
                sb.Append(lines[line].Content).Append(lines[line].EndingText);
            }

            texts[i] = sb.ToString();
        }

        return texts;
    }

    /// <summary>Replaces the given chunks of the result with the session's rendering, as one undoable edit.</summary>
    private void ReplaceChunks(IEnumerable<int> chunks)
    {
        var rendered = RenderedChunks();
        var snapshot = _result.Buffer.CurrentSnapshot;
        var spans = ChunkSpans(snapshot);
        var replace = new HashSet<int>(chunks);

        _applyingEdit = true;
        try
        {
            using (var edit = _result.Buffer.CreateEdit())
            {
                foreach (var i in replace)
                {
                    edit.Replace(Span.FromBounds(spans[i].Start, spans[i].End), rendered[i]);
                }

                snapshot = edit.Apply();
            }
        }
        finally
        {
            _applyingEdit = false;
        }

        // An inserted chunk would otherwise slide past the boundaries that sat where it was inserted.
        var position = 0;
        var starts = new ITrackingPoint[spans.Length];
        for (var i = 0; i < spans.Length; i++)
        {
            starts[i] = snapshot.CreateTrackingPoint(position, PointTrackingMode.Negative);
            position += replace.Contains(i) ? rendered[i].Length : spans[i].End - spans[i].Start;
        }

        _chunkStarts = starts;
    }

    /// <summary>Text typed into the result resolves the conflicts it touches with that text.</summary>
    private void OnResultEdited(object sender, TextContentChangedEventArgs e)
    {
        if (_applyingEdit || _disposed)
        {
            return;
        }

        var spans = ChunkSpans(e.After);
        var touched = new SortedSet<int>();
        foreach (var change in e.Changes)
        {
            for (var i = 0; i < spans.Length; i++)
            {
                var (start, end) = spans[i];
                var inside = change.NewPosition >= start && (change.NewPosition < end || (i == spans.Length - 1 && change.NewPosition <= end));
                var overlaps = change.NewEnd > start && change.NewPosition < end;
                if (inside || overlaps)
                {
                    touched.Add(i);
                }
            }
        }

        var session = _layout.Session;
        foreach (var i in touched)
        {
            var text = e.After.GetText(Span.FromBounds(spans[i].Start, spans[i].End));
            var lines = TextDocument.Parse(text).Lines;

            // Text that still has conflict markers (for example after an undo) is not resolved yet.
            if (lines.Any(line => IsConflictMarker(line.Content)))
            {
                session.Resolve(i, MergeResolution.Unresolved);
            }
            else
            {
                session.SetCustomText(i, lines.Select(line => line.Content).ToList());
            }
        }

        UpdateView();
    }

    private static bool IsConflictMarker(string line) =>
        line.Length >= 7
        && (line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith("=======", StringComparison.Ordinal)
            || line.StartsWith(">>>>>>>", StringComparison.Ordinal) || line.StartsWith("|||||||", StringComparison.Ordinal))
        && (line.Length == 7 || line[7] is ' ' or '\t');

    /// <summary>Recolours the panes and updates the counts after the choices or the result changed.</summary>
    private void UpdateView()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _result.Buffer.CurrentSnapshot;
        var ranges = new (int Start, int Count)[_chunkStarts.Length];
        var spans = ChunkSpans(snapshot);
        for (var i = 0; i < spans.Length; i++)
        {
            var startLine = snapshot.GetLineNumberFromPosition(spans[i].Start);
            ranges[i] = spans[i].End == spans[i].Start
                ? (startLine, 0)
                : (startLine, snapshot.GetLineNumberFromPosition(spans[i].End - 1) - startLine + 1);
        }

        _ranges = ranges;
        var lineCount = snapshot.LineCount;
        if (snapshot.GetLineFromLineNumber(lineCount - 1).Length == 0)
        {
            lineCount--;
        }

        _layout.UpdateLineKinds();
        var resultState = _layout.ResultState(lineCount, ranges);
        DiffPaneState.Set(_result.View, resultState);
        foreach (var pane in new[] { _left, _base, _right, _result })
        {
            Relayout(pane.View);
        }

        _resultHeader.SetLineCount(lineCount);
        _resultHeader.SetDirty(IsDirty);

        var unresolved = _layout.Session.UnresolvedCount;
        if (_conflictCount == 0 && unresolved == 0)
        {
            _verdict.Text = "✔  No conflicts: every change merged automatically";
            _verdict.Foreground = DiffTheme.AddedBrush;
        }
        else if (unresolved == 0)
        {
            _verdict.Text = "✔  All conflicts resolved";
            _verdict.Foreground = DiffTheme.AddedBrush;
        }
        else
        {
            _verdict.Text = $"{unresolved:N0} of {Plural(Math.Max(_conflictCount, unresolved), "conflict")} unresolved";
            _verdict.Foreground = DiffTheme.RemovedBrush;
        }

        UpdatePosition();
    }

    private void ResolveCurrent(MergeResolution resolution)
    {
        var session = _layout.Session;
        if (_currentChunk < 0 || _layout.Result.Chunks[_currentChunk].Kind != MergeChunkKind.Conflict)
        {
            _position.Text = _conflictCount == 0 ? "There are no conflicts to resolve" : "Select a conflict first: click inside it or use Next (F8)";
            return;
        }

        session.Resolve(_currentChunk, resolution);
        ReplaceChunks(new[] { _currentChunk });
        UpdateView();
        RevealCurrentInResult();

        // After a choice, move on to the next conflict that still needs one.
        if (resolution != MergeResolution.Unresolved && session.UnresolvedCount > 0)
        {
            GoToConflict(+1, unresolvedOnly: true);
        }
    }

    private void ResolveAll(MergeResolution resolution)
    {
        _layout.Session.ResolveAllConflicts(resolution);
        ReplaceChunks(Enumerable.Range(0, _layout.Result.Chunks.Count).Where(i => _layout.Result.Chunks[i].Kind == MergeChunkKind.Conflict));
        UpdateView();
    }

    private void GoToConflict(int direction, bool unresolvedOnly)
    {
        var session = _layout.Session;
        var chunks = _layout.Result.Chunks.Count;
        if (_conflictCount == 0)
        {
            return;
        }

        var from = _currentChunk < 0 ? (direction > 0 ? -1 : chunks) : _currentChunk;
        for (var step = 0; step <= chunks; step++)
        {
            var next = direction > 0 ? session.NextConflict(from) : session.PreviousConflict(from);
            if (next < 0)
            {
                // Wrap around only when looking for unresolved conflicts; plain stepping stops at the ends.
                if (!unresolvedOnly)
                {
                    return;
                }

                next = direction > 0 ? session.NextConflict(-1) : session.PreviousConflict(chunks);
            }

            if (!unresolvedOnly || session.Resolution(next) == MergeResolution.Unresolved)
            {
                _currentChunk = next;
                UpdatePosition();
                ScrollToRow(_layout.ChunkFirstRow[next]);
                RevealCurrentInResult();
                return;
            }

            from = next;
        }
    }

    /// <summary>Makes the chunk under the caret the current one, so the take buttons act on what was clicked.</summary>
    private void SelectChunkAt(ITextView view, SnapshotPoint position)
    {
        var state = DiffPaneState.Get(view);
        if (state is null || state.LineCount == 0)
        {
            return;
        }

        var line = Math.Min(position.GetContainingLine().LineNumber, state.LineCount - 1);
        SetCurrentChunk(_layout.ChunkAtRow(state.RowOfLine[line]));
    }

    private void SelectChunkInResult(SnapshotPoint position)
    {
        var line = position.GetContainingLine().LineNumber;
        for (var i = 0; i < _ranges.Count; i++)
        {
            if (line >= _ranges[i].Start && line < _ranges[i].Start + _ranges[i].Count)
            {
                SetCurrentChunk(i);
                return;
            }
        }
    }

    private void SetCurrentChunk(int chunk)
    {
        if (chunk >= 0 && chunk != _currentChunk && _layout.Result.Chunks[chunk].Kind == MergeChunkKind.Conflict)
        {
            _currentChunk = chunk;
            UpdatePosition();
        }
    }

    private void UpdatePosition()
    {
        if (_conflictCount == 0)
        {
            _position.Text = "Nothing to resolve: save to write the merged result";
            return;
        }

        if (_currentChunk < 0 || _layout.Result.Chunks[_currentChunk].Kind != MergeChunkKind.Conflict)
        {
            _position.Text = "Use Next (F8) to step through the conflicts";
            return;
        }

        var number = 0;
        for (var i = 0; i <= _currentChunk; i++)
        {
            if (_layout.Result.Chunks[i].Kind == MergeChunkKind.Conflict)
            {
                number++;
            }
        }

        var choice = _layout.Session.Resolution(_currentChunk) switch
        {
            MergeResolution.Unresolved => "unresolved",
            MergeResolution.Left => "took left",
            MergeResolution.Right => "took right",
            MergeResolution.Base => "kept base",
            MergeResolution.LeftThenRight => "took left, then right",
            MergeResolution.RightThenLeft => "took right, then left",
            _ => "edited by hand",
        };
        _position.Text = $"Conflict {number} of {_conflictCount:N0}  ·  {choice}";
    }

    /// <summary>Saves the merged text to the result file, in the left file's encoding.</summary>
    public void Save()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var warnings = new List<string>();
        if (_overwrittenInput is not null && !_overwriteConfirmed)
        {
            warnings.Add($"The result is saved over {Path.GetFileName(_resultPath)}, the {_overwrittenInput} file being merged. Its current content will be replaced.");
        }

        var text = _result.Buffer.CurrentSnapshot.GetText();
        var unresolved = _layout.Session.UnresolvedCount;
        if (unresolved > 0)
        {
            warnings.Add($"{Plural(unresolved, "conflict")} {(unresolved == 1 ? "is" : "are")} not resolved yet; the file will contain conflict markers.");
        }

        if (warnings.Count > 0)
        {
            var answer = VsShellUtilities.ShowMessageBox(
                ServiceProvider.GlobalProvider,
                string.Join(Environment.NewLine + Environment.NewLine, warnings) + Environment.NewLine + Environment.NewLine + "Save anyway?",
                "DiffRapid",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            if (answer != (int)VSConstants.MessageBoxResult.IDYES)
            {
                return;
            }

            _overwriteConfirmed = true;
        }

        try
        {
            var encoding = _leftFile.Encoding;
            var preamble = _leftFile.HasByteOrderMark ? encoding.GetPreamble() : Array.Empty<byte>();
            var body = encoding.GetBytes(text);
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            File.WriteAllBytes(_resultPath, bytes);
            _savedText = text;
            _resultHeader.SetDirty(false);
            _position.Text = $"Saved {Path.GetFileName(_resultPath)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            _position.Text = $"Could not save {Path.GetFileName(_resultPath)}: {ex.Message}";
        }
    }

    // The window's commands (typing, undo, copy...) go to the editor that has focus.
    private Pane? FocusedPane => new[] { _result, _left, _base, _right }.FirstOrDefault(p => p.HasFocus);

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

    // F8 / Shift+F8 step through conflicts; Ctrl+Shift+F8 and Ctrl+F8 take the left or right version
    // (the same keys copy to the left or right in a file comparison). Save writes the result.
    private Action? OwnCommand(Guid group, uint id)
    {
        if (group == PackageIds.CommandSet)
        {
            return (int)id switch
            {
                PackageIds.PreviousDifference => () => GoToConflict(-1, unresolvedOnly: false),
                PackageIds.NextDifference => () => GoToConflict(+1, unresolvedOnly: false),
                PackageIds.CopyToLeft => () => ResolveCurrent(MergeResolution.Left),
                PackageIds.CopyToRight => () => ResolveCurrent(MergeResolution.Right),
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

    private static string Plural(int count, string singular) => $"{count:N0} {(count == 1 ? singular : singular + "s")}";

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

    /// <summary>Aligned row at the top of a source pane, with the fraction of a row scrolled past.</summary>
    private static double? TopRow(IWpfTextView view)
    {
        var state = DiffPaneState.Get(view);
        if (state is null || state.LineCount == 0 || view.TextViewLines is null)
        {
            return null;
        }

        var first = view.TextViewLines.FirstVisibleLine;
        var line = Math.Min(first.Start.GetContainingLine().LineNumber, state.LineCount - 1);
        return state.BlockTopRow(line) + ((view.ViewportTop - first.Top) / view.LineHeight);
    }

    private static void ShowRow(IWpfTextView view, double topRow)
    {
        var state = DiffPaneState.Get(view);
        if (state is null || state.LineCount == 0 || view.InLayout)
        {
            return;
        }

        var line = state.LineAtOrAfterRow((int)Math.Floor(topRow));
        var offset = (state.BlockTopRow(line) - topRow) * view.LineHeight;
        var snapshotLine = view.TextSnapshot.GetLineFromLineNumber(Math.Min(line, view.TextSnapshot.LineCount - 1));
        view.DisplayTextLineContainingBufferPosition(snapshotLine.Start, offset, ViewRelativePosition.Top);
    }

    private void SyncScroll(IWpfTextView source)
    {
        if (_syncingScroll || source.InLayout || TopRow(source) is not double topRow)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            foreach (var pane in new[] { _left, _base, _right })
            {
                var target = pane.View;
                if (target != source && !target.InLayout && target.TextViewLines is not null)
                {
                    ShowRow(target, topRow);
                    target.ViewportLeft = source.ViewportLeft;
                }
            }

            // The result follows at the same place within the chunk at the top of the source panes, unless
            // it is being edited.
            var chunk = _layout.ChunkAtRow((int)Math.Floor(topRow));
            var result = _result.View;
            if (!_result.HasFocus && chunk >= 0 && chunk < _ranges.Count && !result.InLayout && result.TextViewLines is not null)
            {
                var into = (int)Math.Floor(topRow) - _layout.ChunkFirstRow[chunk];
                var range = _ranges[chunk];
                ShowRow(result, range.Start + Math.Max(0, Math.Min(into, range.Count - 1)));
            }
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void ScrollToRow(int row)
    {
        var view = _left.View;
        if (view.TextViewLines is null)
        {
            return;
        }

        // Leave a third of the pane above the conflict for context.
        ShowRow(view, Math.Max(0, row - (view.ViewportHeight / 3 / view.LineHeight)));
        SyncScroll(view);
    }

    /// <summary>Scrolls the result so the current conflict's lines are in view and puts the caret there.</summary>
    private void RevealCurrentInResult()
    {
        var view = _result.View;
        if (_currentChunk < 0 || _currentChunk >= _ranges.Count || view.TextViewLines is null || view.InLayout)
        {
            return;
        }

        var snapshot = view.TextSnapshot;
        var line = snapshot.GetLineFromLineNumber(Math.Min(_ranges[_currentChunk].Start, snapshot.LineCount - 1));
        _syncingScroll = true;
        try
        {
            view.DisplayTextLineContainingBufferPosition(line.Start, view.ViewportHeight / 3, ViewRelativePosition.Top);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>
    /// One editor of the merge over an in-memory buffer. Made through the adapters so it gets Visual Studio's
    /// editing commands (typing, undo, clipboard, find); the source panes are read-only.
    /// </summary>
    private sealed class Pane
    {
        private readonly IVsTextView _textView;

        private Pane(IVsTextView textView, IWpfTextViewHost host, ITextBuffer buffer)
        {
            _textView = textView;
            Host = host;
            Buffer = buffer;
        }

        public IWpfTextViewHost Host { get; }

        public IWpfTextView View => Host.TextView;

        public ITextBuffer Buffer { get; }

        public IOleCommandTarget CommandTarget => (IOleCommandTarget)_textView;

        public bool HasFocus => Host.HostControl.IsKeyboardFocusWithin;

        public static Pane Create(string text, bool readOnly, IContentType contentType, IComponentModel componentModel, OleServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var editorFactory = componentModel.GetService<ITextEditorFactoryService>();

            var textBuffer = adapters.CreateVsTextBufferAdapter(services, contentType);
            ErrorHandler.ThrowOnFailure(textBuffer.InitializeContent(text, text.Length));
            if (readOnly)
            {
                ErrorHandler.ThrowOnFailure(textBuffer.GetStateFlags(out var flags));
                ErrorHandler.ThrowOnFailure(textBuffer.SetStateFlags(flags | (uint)BUFFERSTATEFLAGS.BSF_USER_READONLY));
            }

            var roles = editorFactory.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Document,
                PredefinedTextViewRoles.Analyzable,
                PredefinedTextViewRoles.Editable,
                PredefinedTextViewRoles.Interactive,
                PredefinedTextViewRoles.Zoomable,
                DiffRapidRoles.Pane);
            var textView = adapters.CreateVsTextViewAdapter(services, roles);
            var init = new[] { new INITVIEW { fSelectionMargin = 0, fWidgetMargin = 0, fVirtualSpace = 0, fDragDropMove = 1 } };
            const uint viewFlags = (uint)TextViewInitFlags.VIF_HSCROLL | (uint)TextViewInitFlags.VIF_VSCROLL | (uint)TextViewInitFlags3.VIF_NO_HWND_SUPPORT;
            ErrorHandler.ThrowOnFailure(textView.Initialize((IVsTextLines)textBuffer, IntPtr.Zero, viewFlags, init));

            // As in the file comparison: without the Text Editor font and colour category, syntax colours
            // fall back to the light-theme defaults.
            var categories = (IVsTextEditorPropertyCategoryContainer)textView;
            var masterSettings = DefGuidList.guidEditPropCategoryViewMasterSettings;
            ErrorHandler.ThrowOnFailure(categories.GetPropertyCategory(ref masterSettings, out var properties));
            ErrorHandler.ThrowOnFailure(properties.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_FontCategory, DefGuidList.guidTextEditorFontCategory));
            ErrorHandler.ThrowOnFailure(properties.SetProperty(VSEDITPROPID.VSEDITPROPID_ViewGeneral_ColorCategory, DefGuidList.guidTextEditorFontCategory));

            var host = adapters.GetWpfTextViewHost(textView)
                ?? throw new InvalidOperationException("The editor view has no WPF host.");
            var view = host.TextView;
            view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            return new Pane(textView, host, adapters.GetDataBuffer(textBuffer)
                ?? throw new InvalidOperationException("The editor buffer has no text buffer."));
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!Host.IsClosed)
            {
                _textView.CloseView();
            }
        }
    }
}
