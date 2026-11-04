using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using DiffRapid.Core.Patching;
using DiffRapid.Core.Text;
using DiffRapid.VisualStudio.Commands;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Previews a patch against a folder (or one file) and writes the checked files on request. The preview
/// never touches the disk; double-clicking a file compares it before and after the patch.
/// </summary>
internal sealed class ApplyPatchControl : DockPanel, IComparisonView
{
    private static readonly int[] FuzzLevels = { 0, 1, 2 };

    // The options last used, so the next patch starts the same way.
    private static PatchApplyOptions _lastOptions = new();

    private readonly DiffRapidPackage _package;
    private readonly string _patchPath;
    private readonly string _targetPath;
    private readonly ListView _list = new();
    private readonly Button _apply;
    private readonly Button _compare;
    private readonly TextBlock _verdict = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _details = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private PatchApplyOptions _options = _lastOptions;
    private CancellationTokenSource? _planCancellation;
    private FolderPatchPlan? _plan;
    private bool _busy;
    private bool _disposed;

    public ApplyPatchControl(DiffRapidPackage package, string patchPath, string targetPath)
    {
        _package = package;
        _patchPath = Path.GetFullPath(patchPath);
        _targetPath = Path.GetFullPath(targetPath);

        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

        _apply = DiffTheme.CommandButton(IconLabel(KnownMonikers.Checkmark, "Apply checked"), "Write the checked files (Ctrl+S). Files changed on disk since the preview are skipped.", ApplyChecked);
        _apply.IsEnabled = false;
        _compare = DiffTheme.CommandButton(IconLabel(KnownMonikers.CompareFiles, "Compare"), "Compare the selected file before and after the patch (Enter or double-click)", CompareSelected);
        _compare.IsEnabled = false;

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
        StartPlan(null);
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
        _planCancellation?.Cancel();
    }

    private IEnumerable<PatchRow> Rows => _list.Items.OfType<PatchRow>();

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 4, 6, 4) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(DiffTheme.CommandButton(Icon(KnownMonikers.Refresh), "Preview again (F5)", () => StartPlan(null)));
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_apply);
        actions.Children.Add(_compare);
        actions.Children.Add(DiffTheme.Separator());
        actions.Children.Add(_verdict);
        SetDock(actions, Dock.Left);
        bar.Children.Add(actions);

        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        var reverse = DiffTheme.OptionToggle("Reverse", "Undo the patch: apply it from the new side back to the old side", on => ChangeOptions(_options with { Reverse = on }), _options.Reverse);
        var whitespace = DiffTheme.OptionToggle("Ignore whitespace", "Match the patch's lines to the files even when their spacing differs", on => ChangeOptions(_options with { IgnoreWhitespace = on }), _options.IgnoreWhitespace);
        settings.Children.Add(reverse);
        settings.Children.Add(whitespace);
        settings.Children.Add(DiffTheme.Separator());
        settings.Children.Add(DiffTheme.Caption("FUZZ"));
        var fuzz = new ComboBox { MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How many context lines at the edge of a change may differ from the file (0 = exact, 2 = most forgiving)" };
        fuzz.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var level in FuzzLevels)
        {
            fuzz.Items.Add(level.ToString());
        }

        fuzz.SelectedIndex = Math.Max(0, Array.IndexOf(FuzzLevels, _options.Fuzz));
        fuzz.SelectionChanged += (_, _) => ChangeOptions(_options with { Fuzz = FuzzLevels[fuzz.SelectedIndex] });
        settings.Children.Add(fuzz);
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var patch = PathLabel("PATCH", _patchPath, DiffTheme.MovedBrush);
        var target = PathLabel(File.Exists(_targetPath) ? "FILE" : "FOLDER", _targetPath, DiffTheme.ChangedBrush);
        Grid.SetColumn(target, 1);
        grid.Children.Add(patch);
        grid.Children.Add(target);

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
        view.Columns.Add(new GridViewColumn { Header = string.Empty, Width = 32, CellTemplate = CheckTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "File", Width = 360, DisplayMemberBinding = new Binding(nameof(PatchRow.Path)) });
        view.Columns.Add(new GridViewColumn { Header = "Change", Width = 90, DisplayMemberBinding = new Binding(nameof(PatchRow.ActionText)) });
        view.Columns.Add(new GridViewColumn { Header = "Status", Width = 130, CellTemplate = StatusTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Hunks", Width = 220, DisplayMemberBinding = new Binding(nameof(PatchRow.HunksText)) });
        view.Columns.Add(new GridViewColumn { Header = "Details", Width = 360, DisplayMemberBinding = new Binding(nameof(PatchRow.Details)) });

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
                CompareSelected();
            }
        };
        return _list;
    }

    // A check box per file; only files that can be written can be checked.
    private DataTemplate CheckTemplate()
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PatchRow.IsChecked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(IsEnabledProperty, new Binding(nameof(PatchRow.CanApply)));
        box.AddHandler(ToggleButton.ClickEvent, new RoutedEventHandler((_, _) => UpdateButtons()));
        return new DataTemplate { VisualTree = box };
    }

    // A soft pill in the status colour, as in the folder comparison.
    private static DataTemplate StatusTemplate()
    {
        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        pill.SetValue(Border.PaddingProperty, new Thickness(8, 1, 8, 1));
        pill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(PatchRow.StatusTint)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(PatchRow.StatusText)));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PatchRow.StatusBrush)));
        pill.AppendChild(text);
        return new DataTemplate { VisualTree = pill };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            StartPlan(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _list.IsKeyboardFocusWithin)
        {
            CompareSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ApplyChecked();
            e.Handled = true;
        }
    }

    private void ChangeOptions(PatchApplyOptions options)
    {
        _options = options;
        _lastOptions = options;
        StartPlan(null);
    }

    /// <summary>Reads and plans the patch in the background; <paramref name="message"/> stays in the footer afterwards.</summary>
    private void StartPlan(string? message)
    {
        _planCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _planCancellation = cancellation;
        var options = _options;
        var keepUnchecked = new HashSet<string>(Rows.Where(r => r.CanApply && !r.IsChecked).Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        SetBusy(true, "Reading the patch…");

        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            FolderPatchPlan? plan = null;
            string? problem = null;
            try
            {
                plan = await Task.Run(() => BuildPlan(options), cancellation.Token);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problem = ex.Message;
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed || cancellation.IsCancellationRequested)
            {
                return;
            }

            ShowPlan(plan, keepUnchecked);
            SetBusy(false, message ?? problem ?? Summary(plan));
        }).FileAndForget("DiffRapid/ApplyPatchPreview");
    }

    private FolderPatchPlan BuildPlan(PatchApplyOptions options)
    {
        var patch = Patch.Parse(TextFileReader.ReadFile(_patchPath).Text);
        if (patch.Files.Count == 0)
        {
            throw new InvalidOperationException("The file does not contain a patch (no ---/+++ or diff --git headers were found).");
        }

        if (!File.Exists(_targetPath))
        {
            return FolderPatchApplier.Plan(patch, _targetPath, options);
        }

        // A single target file takes a one-file patch whatever names the patch uses.
        if (patch.Files.Count != 1)
        {
            throw new InvalidOperationException($"The patch changes {patch.Files.Count} files. Apply it to a folder instead of a single file.");
        }

        var file = FolderPatchApplier.PlanFile(patch.Files[0], _targetPath, options);
        return new FolderPatchPlan(Path.GetDirectoryName(_targetPath)!, options, new[] { file });
    }

    private void ShowPlan(FolderPatchPlan? plan, ISet<string> keepUnchecked)
    {
        _plan = plan;
        var selected = (_list.SelectedItem as PatchRow)?.Path;
        var rows = plan?.Files.Select(f => new PatchRow(f) { IsChecked = f.CanApply && !keepUnchecked.Contains(f.DisplayPath) }).ToList() ?? new List<PatchRow>();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Path == selected) ?? rows.FirstOrDefault();

        var ready = rows.Count(r => r.Plan.Status == FilePatchStatus.Ready);
        var done = rows.Count(r => r.Plan.Status == FilePatchStatus.AlreadyApplied);
        var problems = rows.Count - ready - done;
        _verdict.Text = plan is null ? "Cannot read the patch"
            : problems > 0 && (ready > 0 || done > 0) ? string.Join("  ·  ", new[] { ready > 0 ? $"{ready} ready" : null, done > 0 ? $"{done} already applied" : null, $"{problems} cannot be applied" }.Where(p => p is not null))
            : problems > 0 ? $"{problems} of {rows.Count} files cannot be applied"
            : ready > 0 ? $"{ready} of {rows.Count} files ready"
            : "Already applied";
        _verdict.Foreground = plan is null || problems > 0 ? DiffTheme.RemovedBrush : ready > 0 ? DiffTheme.AddedBrush : DiffTheme.IgnoredBrush;
        UpdateButtons();
    }

    private static string Summary(FolderPatchPlan? plan)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        var hunks = plan.Files.Sum(f => f.Outcomes.Count);
        return $"{plan.Files.Count} files, {hunks} hunks. Nothing has been written yet: check the files to apply, then choose Apply checked.";
    }

    private void UpdateButtons()
    {
        _apply.IsEnabled = !_busy && Rows.Any(r => r.IsChecked && r.CanApply);
        _compare.IsEnabled = !_busy && _list.SelectedItem is PatchRow { Plan.Status: not (FilePatchStatus.Binary or FilePatchStatus.Unsafe) };
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        _details.Text = message;
        Cursor = busy ? Cursors.AppStarting : null;
        UpdateButtons();
    }

    /// <summary>Opens a comparison of the selected file before (left) and after (right) the patch.</summary>
    private void CompareSelected()
    {
        if (_list.SelectedItem is not PatchRow row || row.Plan.Status is FilePatchStatus.Binary or FilePatchStatus.Unsafe)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(row.Path);
        var extension = Path.GetExtension(row.Path);
        var before = EditorSelection.WriteTemporary(row.Plan.OriginalText, name + " (before patch)", extension);
        var after = EditorSelection.WriteTemporary(row.Plan.PatchedText, name + " (patched)", extension);
        _package.JoinableTaskFactory.RunAsync(() => _package.OpenComparisonAsync(before, after)).FileAndForget("DiffRapid/ApplyPatchCompare");
    }

    private void ApplyChecked()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_busy || _plan is not { } plan)
        {
            return;
        }

        var chosen = new HashSet<FilePatchPlan>(Rows.Where(r => r.IsChecked && r.CanApply).Select(r => r.Plan));
        if (chosen.Count == 0)
        {
            return;
        }

        // Writing under an open document with unsaved edits would make Visual Studio ask which version to keep.
        var unsaved = chosen.SelectMany(f => new[] { f.SourcePath, f.TargetPath }).OfType<string>().Where(IsOpenAndDirty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unsaved.Count > 0)
        {
            _details.Text = "Save or close these documents first: " + string.Join(", ", unsaved.Select(Path.GetFileName));
            return;
        }

        SetBusy(true, $"Applying {chosen.Count} files…");
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            var results = await Task.Run(() => FolderPatchApplier.Apply(plan, chosen.Contains));
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed)
            {
                return;
            }

            StartPlan(Outcome(results));
        }).FileAndForget("DiffRapid/ApplyPatch");
    }

    private static string Outcome(IReadOnlyList<FilePatchWriteResult> results)
    {
        var written = results.Count(r => r.Written);
        var failed = results.Where(r => !r.Written && r.Error is not null).ToList();
        var message = $"Applied {written} of {results.Count} files.";
        if (failed.Count > 0)
        {
            message += " Not written: " + string.Join("; ", failed.Select(r => $"{r.File.DisplayPath} ({r.Error})"));
        }

        return message;
    }

    private bool IsOpenAndDirty(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var table = new RunningDocumentTable(_package);
        return table.FindDocument(path) is IVsPersistDocData document
            && document.IsDocDataDirty(out var dirty) == VSConstants.S_OK
            && dirty != 0;
    }

    private static CrispImage Icon(ImageMoniker moniker) => new() { Moniker = moniker, Width = 16, Height = 16 };

    private static StackPanel IconLabel(ImageMoniker moniker, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Icon(moniker));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    /// <summary>One file of the patch as shown in the list.</summary>
    private sealed class PatchRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public PatchRow(FilePatchPlan plan)
        {
            Plan = plan;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public FilePatchPlan Plan { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public bool CanApply => Plan.CanApply;

        public string Path => Plan.DisplayPath;

        public string ActionText => Plan.Action switch
        {
            FilePatchAction.Add => "Add",
            FilePatchAction.Delete => "Delete",
            FilePatchAction.Rename => "Rename",
            _ => "Change",
        };

        public string StatusText => Plan.Status switch
        {
            FilePatchStatus.Ready => "Ready",
            FilePatchStatus.AlreadyApplied => "Already applied",
            FilePatchStatus.Binary => "Binary",
            FilePatchStatus.Unsafe => "Unsafe path",
            _ => "Does not match",
        };

        public Brush StatusBrush => Plan.Status switch
        {
            FilePatchStatus.Ready => DiffTheme.AddedBrush,
            FilePatchStatus.AlreadyApplied => DiffTheme.IgnoredBrush,
            FilePatchStatus.Binary => DiffTheme.MovedBrush,
            _ => DiffTheme.RemovedBrush,
        };

        public Brush StatusTint => DiffTheme.Tint(StatusBrush);

        public string HunksText
        {
            get
            {
                var outcomes = Plan.Outcomes;
                if (outcomes.Count == 0)
                {
                    return Plan.Action == FilePatchAction.Rename ? "No content change" : string.Empty;
                }

                var parts = new List<string> { outcomes.Count == 1 ? "1 hunk" : $"{outcomes.Count} hunks" };
                AddCount(parts, outcomes.Count(o => o.Status == HunkStatus.AppliedWithOffset), "moved");
                AddCount(parts, outcomes.Count(o => o.Status == HunkStatus.AppliedWithFuzz), "fuzzy");
                AddCount(parts, outcomes.Count(o => o.Status == HunkStatus.AlreadyApplied), "already in");
                AddCount(parts, outcomes.Count(o => o.Status == HunkStatus.Failed), "failed");
                return string.Join(" · ", parts);
            }
        }

        // The File column already names both paths of a rename.
        public string Details => Plan.Message ?? string.Empty;

        private static void AddCount(List<string> parts, int count, string label)
        {
            if (count > 0)
            {
                parts.Add($"{count} {label}");
            }
        }
    }
}
