using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.VisualStudio.Editor;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Side-by-side text comparison built from two Visual Studio editor views. The views show the live
/// document buffers (including unsaved edits), so syntax highlighting comes from VS itself.
/// </summary>
internal sealed class TextDiffControl : DockPanel, IDisposable
{
    private static readonly TimeSpan RecompareDelay = TimeSpan.FromMilliseconds(300);

    private readonly AsyncPackage _package;
    private readonly Pane _left;
    private readonly Pane _right;
    private readonly DiffOverviewBar _overview = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly DispatcherTimer _recompareTimer;
    private CompareOptions _options = CompareOptions.Default;
    private CancellationTokenSource? _compareCancellation;
    private DiffModel? _model;
    private bool _syncingScroll;
    private bool _disposed;

    private TextDiffControl(AsyncPackage package, Pane left, Pane right)
    {
        _package = package;
        _left = left;
        _right = right;

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        Focusable = true;

        _recompareTimer = new DispatcherTimer(RecompareDelay, DispatcherPriority.Background, (_, _) => StartCompare(), Dispatcher);
        _recompareTimer.Stop();

        var toolbar = BuildToolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(BuildPanes());

        _left.Buffer.Changed += OnBufferChanged;
        _right.Buffer.Changed += OnBufferChanged;
        _left.View.LayoutChanged += (_, _) => SyncScroll(_left, _right);
        _right.View.LayoutChanged += (_, _) => SyncScroll(_right, _left);
        _overview.RowRequested += (_, row) => ScrollToRow(row);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public static async Task<TextDiffControl> CreateAsync(AsyncPackage package, string leftPath, string rightPath)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsureText(leftPath);
        EnsureText(rightPath);

        var componentModel = (IComponentModel)(await package.GetServiceAsync(typeof(SComponentModel))
            ?? throw new InvalidOperationException("Component model is unavailable."));
        var invisibleEditors = (IVsInvisibleEditorManager)(await package.GetServiceAsync(typeof(SVsInvisibleEditorManager))
            ?? throw new InvalidOperationException("Invisible editor manager is unavailable."));
        var editorFactory = componentModel.GetService<ITextEditorFactoryService>();
        var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();

        var left = Pane.Open(leftPath, invisibleEditors, adapters, editorFactory);
        var right = Pane.Open(rightPath, invisibleEditors, adapters, editorFactory);
        var control = new TextDiffControl(package, left, right);
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
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };

        bar.Children.Add(Button("▲ Previous", "Previous difference (Alt+Up)", () => NavigateDifference(-1)));
        bar.Children.Add(Button("▼ Next", "Next difference (Alt+Down)", () => NavigateDifference(+1)));
        bar.Children.Add(_status);

        var algorithm = new ComboBox { Margin = new Thickness(8, 0, 0, 0), MinWidth = 110, ToolTip = "Diff algorithm" };
        algorithm.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var kind in (DiffAlgorithmKind[])Enum.GetValues(typeof(DiffAlgorithmKind)))
        {
            algorithm.Items.Add(kind);
        }

        algorithm.SelectedItem = _options.Algorithm;
        algorithm.SelectionChanged += (_, _) => UpdateOptions(_options with { Algorithm = (DiffAlgorithmKind)algorithm.SelectedItem });
        bar.Children.Add(algorithm);

        bar.Children.Add(Toggle("Ignore whitespace", v => _options with { Whitespace = v ? WhitespaceMode.IgnoreChange : WhitespaceMode.Compare }));
        bar.Children.Add(Toggle("Ignore case", v => _options with { IgnoreCase = v }));
        bar.Children.Add(Toggle("Ignore blank lines", v => _options with { IgnoreBlankLines = v }));
        bar.Children.Add(Toggle("Ignore line endings", v => _options with { IgnoreLineEndings = v }));
        return bar;
    }

    private UIElement BuildPanes()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddCell(grid, Header(_left.Path), 0, 0);
        AddCell(grid, Header(_right.Path), 0, 2);
        AddCell(grid, _left.Host.HostControl, 1, 0);
        AddCell(grid, new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext }, 1, 1);
        AddCell(grid, _right.Host.HostControl, 1, 2);
        AddCell(grid, _overview, 1, 3);
        return grid;
    }

    private static void AddCell(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Header(string path) => new()
    {
        Text = path,
        ToolTip = path,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(6, 2, 6, 4),
    };

    private static Button Button(string text, string tooltip, Action onClick)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6, 1, 6, 1) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ButtonStyleKey);
        button.Click += (_, _) => onClick();
        return button;
    }

    private CheckBox Toggle(string text, Func<bool, CompareOptions> apply)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(StyleProperty, VsResourceKeys.CheckBoxStyleKey);
        box.Checked += (_, _) => UpdateOptions(apply(true));
        box.Unchecked += (_, _) => UpdateOptions(apply(false));
        return box;
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

        var leftText = _left.Buffer.CurrentSnapshot.GetText();
        var rightText = _right.Buffer.CurrentSnapshot.GetText();
        var options = _options;
        _status.Text = "Comparing…";

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var model = await Task.Run(() => DiffModel.Build(leftText, rightText, options, cancellation.Token), cancellation.Token);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested)
                {
                    Apply(model);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _status.Text = "Compare failed: " + ex.Message;
            }
        }).FileAndForget("DiffRapid/Compare");
    }

    private void Apply(DiffModel model)
    {
        _model = model;
        DiffPaneState.Set(_left.View, model.Left);
        DiffPaneState.Set(_right.View, model.Right);
        Relayout(_left.View);
        Relayout(_right.View);
        SyncScroll(_left, _right);
        _overview.SetModel(model);

        var count = model.HunkRows.Count;
        var moved = model.Result.MovedBlocks.Count;
        _status.Text = count == 0
            ? "Identical"
            : $"{count} difference{(count == 1 ? string.Empty : "s")}" + (moved > 0 ? $", {moved} moved" : string.Empty);
    }

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
            foreach (var row in model.HunkRows)
            {
                if (row > anchorRow)
                {
                    target = row;
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
                    target = model.HunkRows[i];
                    break;
                }
            }
        }

        if (target >= 0)
        {
            ScrollToRow(target);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Down or Key.Up)
        {
            NavigateDifference(e.SystemKey == Key.Down ? +1 : -1);
            e.Handled = true;
        }
    }

    /// <summary>One side of the comparison: the document buffer and the editor view showing it.</summary>
    private sealed class Pane
    {
        private readonly IVsInvisibleEditor _invisibleEditor;

        private Pane(string path, IVsInvisibleEditor invisibleEditor, ITextBuffer buffer, IWpfTextViewHost host)
        {
            Path = path;
            _invisibleEditor = invisibleEditor;
            Buffer = buffer;
            Host = host;
        }

        public string Path { get; }

        public ITextBuffer Buffer { get; }

        public IWpfTextViewHost Host { get; }

        public IWpfTextView View => Host.TextView;

        public static Pane Open(
            string path,
            IVsInvisibleEditorManager invisibleEditors,
            IVsEditorAdaptersFactoryService adapters,
            ITextEditorFactoryService editorFactory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(invisibleEditors.RegisterInvisibleEditor(
                path, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out var invisibleEditor));

            var iid = typeof(IVsTextLines).GUID;
            ErrorHandler.ThrowOnFailure(invisibleEditor.GetDocData(0, ref iid, out var docDataPtr));
            ITextBuffer buffer;
            try
            {
                var textLines = (IVsTextLines)Marshal.GetObjectForIUnknown(docDataPtr);
                buffer = adapters.GetDocumentBuffer(textLines)
                    ?? throw new InvalidOperationException($"Could not load '{path}' into an editor buffer.");
            }
            finally
            {
                Marshal.Release(docDataPtr);
            }

            var roles = editorFactory.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Document,
                PredefinedTextViewRoles.Analyzable,
                PredefinedTextViewRoles.Interactive,
                PredefinedTextViewRoles.Zoomable,
                DiffRapidRoles.Pane);
            var view = editorFactory.CreateTextView(buffer, roles);
            view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
            view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, true);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            var host = editorFactory.CreateTextViewHost(view, setFocus: false);
            return new Pane(path, invisibleEditor, buffer, host);
        }

        public void Close()
        {
            if (!Host.IsClosed)
            {
                Host.Close();
            }

            GC.KeepAlive(_invisibleEditor);
        }
    }
}
