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
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using Span = Microsoft.VisualStudio.Text.Span;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Three-way merge: the left, base and right versions side by side, aligned row by row, above the merged
/// result. Changes made on one side only are taken automatically; each conflict is resolved by choosing a
/// side (or both) and the result is saved to the chosen file.
/// </summary>
internal sealed class MergeControl : DockPanel, IComparisonView, IOleCommandTarget
{
    private readonly MergeLayout _layout;
    private readonly ConflictLabels _labels;
    private readonly string _resultPath;
    private readonly string? _overwrittenInput;
    private readonly DecodedText _leftFile;
    private readonly IWpfTextViewHost _left;
    private readonly IWpfTextViewHost _base;
    private readonly IWpfTextViewHost _right;
    private readonly IWpfTextViewHost _result;
    private readonly FileHeader _resultHeader;
    private readonly int _conflictCount;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private IReadOnlyList<(int Start, int Count)> _ranges = Array.Empty<(int, int)>();
    private string _mergedText = string.Empty;
    private string? _savedText;
    private int _currentChunk = -1;
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
        string baseText,
        string rightText,
        string? savedText,
        Func<string, IWpfTextViewHost> createView)
    {
        _layout = layout;
        _resultPath = resultPath;
        _overwrittenInput = new[] { (leftPath, "left"), (basePath, "base"), (rightPath, "right") }
            .Where(input => string.Equals(Path.GetFullPath(input.Item1), Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
            .Select(input => input.Item2)
            .FirstOrDefault();
        _leftFile = leftFile;
        _savedText = savedText;
        _labels = new ConflictLabels(Path.GetFileName(leftPath), Path.GetFileName(basePath), Path.GetFileName(rightPath));
        _conflictCount = layout.Result.Chunks.Count(c => c.Kind == MergeChunkKind.Conflict);

        _left = createView(leftFile.Text);
        _base = createView(baseText);
        _right = createView(rightText);
        _result = createView(string.Empty);
        DiffPaneState.Set(_left.TextView, layout.Left);
        DiffPaneState.Set(_base.TextView, layout.Base);
        DiffPaneState.Set(_right.TextView, layout.Right);

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
            source.TextView.LayoutChanged += (_, _) => SyncScroll(source.TextView);
            source.TextView.Caret.PositionChanged += (_, e) => SelectChunkAt(source.TextView, e.NewPosition.BufferPosition);
        }

        _result.TextView.Caret.PositionChanged += (_, e) => SelectChunkInResult(e.NewPosition.BufferPosition);
        _layout.Session.Changed += (_, _) => Refresh();
        Refresh();

        _details.Text = $"{Plural(_conflictCount, "conflict")}  ·  {Plural(layout.Result.Chunks.Count(c => c.Kind is MergeChunkKind.LeftOnly or MergeChunkKind.RightOnly or MergeChunkKind.BothSame), "change")} merged automatically";
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
        var editorFactory = componentModel.GetService<ITextEditorFactoryService>();
        var bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
        var contentType = ContentTypeFor(resultPath, componentModel);

        IWpfTextViewHost CreateView(string text)
        {
            var buffer = bufferFactory.CreateTextBuffer(text, contentType);
            var roles = editorFactory.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Document,
                PredefinedTextViewRoles.Interactive,
                PredefinedTextViewRoles.Zoomable,
                DiffRapidRoles.Pane);
            var view = editorFactory.CreateTextView(buffer, roles);
            view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
            view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
            var host = editorFactory.CreateTextViewHost(view, setFocus: false);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            return host;
        }

        return Diagnostics.Step("Building the merge view", () =>
            new MergeControl(layout, leftPath, basePath, rightPath, resultPath, left, @base.Text, right.Text, saved, CreateView));
    }

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
        foreach (var host in new[] { _left, _base, _right, _result })
        {
            host.Close();
        }
    }

    public bool IsDirty => !string.Equals(_mergedText, _savedText, StringComparison.Ordinal);

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
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToPrevious, "Previous"), "Previous conflict", () => GoToConflict(-1, unresolvedOnly: false)));
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.GoToNext, "Next"), "Next conflict", () => GoToConflict(+1, unresolvedOnly: false)));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(DiffTheme.Caption("TAKE"));
        commands.Children.Add(DiffTheme.CommandButton(Label("Left"), "Resolve the current conflict with the left version", () => ResolveCurrent(MergeResolution.Left)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Base"), "Resolve the current conflict by keeping the base version (neither change)", () => ResolveCurrent(MergeResolution.Base)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Right"), "Resolve the current conflict with the right version", () => ResolveCurrent(MergeResolution.Right)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Left + right"), "Resolve the current conflict with the left lines followed by the right lines", () => ResolveCurrent(MergeResolution.LeftThenRight)));
        commands.Children.Add(DiffTheme.CommandButton(Label("Right + left"), "Resolve the current conflict with the right lines followed by the left lines", () => ResolveCurrent(MergeResolution.RightThenLeft)));
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Undo, "Reset"), "Mark the current conflict as unresolved again", () => ResolveCurrent(MergeResolution.Unresolved)));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(DiffTheme.CommandButton(IconLabel(KnownMonikers.Save, "Save"), "Save the merged result (Ctrl+S)", Save));
        commands.Children.Add(DiffTheme.Separator());
        commands.Children.Add(_verdict);
        SetDock(commands, Dock.Left);
        bar.Children.Add(commands);

        var all = new StackPanel { Orientation = Orientation.Horizontal };
        all.Children.Add(DiffTheme.Caption("ALL CONFLICTS"));
        all.Children.Add(DiffTheme.CommandButton(Label("Left"), "Resolve every conflict with the left version", () => _layout.Session.ResolveAllConflicts(MergeResolution.Left)));
        all.Children.Add(DiffTheme.CommandButton(Label("Right"), "Resolve every conflict with the right version", () => _layout.Session.ResolveAllConflicts(MergeResolution.Right)));
        all.Children.Add(DiffTheme.CommandButton(Label("Reset"), "Mark every conflict as unresolved again", () => _layout.Session.ResolveAllConflicts(MergeResolution.Unresolved)));
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
        foreach (var (header, host) in new (FileHeader, IWpfTextViewHost)[] { (leftHeader, _left), (baseHeader, _base), (rightHeader, _right) })
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
            AddCell(sources, host.HostControl, 1, column++);
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
        AddCell(grid, _result.HostControl, 3, 0);
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

    /// <summary>Re-renders the result and the line colours after the choices changed.</summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var session = _layout.Session;
        _mergedText = session.ToText(_labels);
        _ranges = session.OutputLineRanges(_labels);
        _layout.UpdateLineKinds();

        var buffer = _result.TextView.TextBuffer;
        var resultState = _layout.ResultState(TextDocument.Parse(_mergedText).LineCount, _ranges);
        DiffPaneState.Set(_result.TextView, resultState);
        buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), _mergedText);

        foreach (var host in new[] { _left, _base, _right, _result })
        {
            Relayout(host.TextView);
        }

        _resultHeader.SetLineCount(resultState.LineCount);
        _resultHeader.SetDirty(IsDirty);

        var unresolved = session.UnresolvedCount;
        if (_conflictCount == 0)
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
            _verdict.Text = $"{unresolved:N0} of {Plural(_conflictCount, "conflict")} unresolved";
            _verdict.Foreground = DiffTheme.RemovedBrush;
        }

        UpdatePosition();
        RevealCurrentInResult();
    }

    private void ResolveCurrent(MergeResolution resolution)
    {
        var session = _layout.Session;
        if (_currentChunk < 0 || _layout.Result.Chunks[_currentChunk].Kind != MergeChunkKind.Conflict)
        {
            _position.Text = _conflictCount == 0 ? "There are no conflicts to resolve" : "Select a conflict first: click inside it or use Next";
            return;
        }

        var chunk = _currentChunk;
        session.Resolve(chunk, resolution);

        // After a choice, move on to the next conflict that still needs one.
        if (resolution != MergeResolution.Unresolved && session.UnresolvedCount > 0)
        {
            GoToConflict(+1, unresolvedOnly: true);
        }
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
            _position.Text = "Use Next to step through the conflicts";
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

        var resolution = _layout.Session.Resolution(_currentChunk);
        var choice = resolution switch
        {
            MergeResolution.Unresolved => "unresolved",
            MergeResolution.Left => "took left",
            MergeResolution.Right => "took right",
            MergeResolution.Base => "kept base",
            MergeResolution.LeftThenRight => "took left, then right",
            MergeResolution.RightThenLeft => "took right, then left",
            _ => "edited",
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
            var body = encoding.GetBytes(_mergedText);
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            File.WriteAllBytes(_resultPath, bytes);
            _savedText = _mergedText;
            _resultHeader.SetDirty(false);
            _position.Text = $"Saved {Path.GetFileName(_resultPath)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            _position.Text = $"Could not save {Path.GetFileName(_resultPath)}: {ex.Message}";
        }
    }

    // The window's commands: Save writes the result; Copy copies the selection of the pane with focus.
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

    private Action? OwnCommand(Guid group, uint id)
    {
        if (group != VSConstants.GUID_VSStandardCommandSet97)
        {
            return null;
        }

        return (VSConstants.VSStd97CmdID)id switch
        {
            VSConstants.VSStd97CmdID.Save or VSConstants.VSStd97CmdID.SaveProjectItem => Save,
            VSConstants.VSStd97CmdID.Copy => CopySelection,
            _ => null,
        };
    }

    private void CopySelection()
    {
        var focused = new[] { _left, _base, _right, _result }.FirstOrDefault(h => h.HostControl.IsKeyboardFocusWithin);
        var selection = focused?.TextView.Selection;
        if (selection is null || selection.IsEmpty)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, selection.SelectedSpans.Select(s => s.GetText()));
        Clipboard.SetText(text);
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
            foreach (var host in new[] { _left, _base, _right })
            {
                var target = host.TextView;
                if (target != source && !target.InLayout && target.TextViewLines is not null)
                {
                    ShowRow(target, topRow);
                    target.ViewportLeft = source.ViewportLeft;
                }
            }

            // The result follows at the same place within the chunk at the top of the source panes.
            var chunk = _layout.ChunkAtRow((int)Math.Floor(topRow));
            if (chunk >= 0 && chunk < _ranges.Count && !_result.TextView.InLayout && _result.TextView.TextViewLines is not null)
            {
                var into = (int)Math.Floor(topRow) - _layout.ChunkFirstRow[chunk];
                var range = _ranges[chunk];
                var line = range.Start + Math.Max(0, Math.Min(into, range.Count - 1));
                ShowRow(_result.TextView, line);
            }
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void ScrollToRow(int row)
    {
        var view = _left.TextView;
        if (view.TextViewLines is null)
        {
            return;
        }

        // Leave a third of the pane above the conflict for context.
        ShowRow(view, Math.Max(0, row - (view.ViewportHeight / 3 / view.LineHeight)));
        SyncScroll(view);
    }

    /// <summary>Scrolls the result so the current conflict's lines are in view.</summary>
    private void RevealCurrentInResult()
    {
        var view = _result.TextView;
        if (_currentChunk < 0 || _currentChunk >= _ranges.Count || view.TextViewLines is null || view.InLayout)
        {
            return;
        }

        var start = _ranges[_currentChunk].Start;
        var snapshot = view.TextSnapshot;
        var line = snapshot.GetLineFromLineNumber(Math.Min(start, snapshot.LineCount - 1));
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
}
