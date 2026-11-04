using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Sync;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Chooses how to synchronize the two compared folders and previews every operation before anything is written.
/// Planning only uses the comparison results, so switching profiles is instant.
/// </summary>
internal sealed class SyncDialog : DialogWindow
{
    private static readonly (string Label, string Description, SyncMode Mode)[] Modes =
    {
        ("Mirror left → right", "Make the right folder an exact copy of the left: copies, overwrites and deletes on the right.", SyncMode.MirrorLeftToRight),
        ("Mirror right → left", "Make the left folder an exact copy of the right: copies, overwrites and deletes on the left.", SyncMode.MirrorRightToLeft),
        ("Update right", "Copy files that are missing or newer on the left to the right. Nothing is deleted.", SyncMode.Update),
        ("Update both", "Copy missing and newer files in both directions. Nothing is deleted; files changed on both sides are conflicts.", SyncMode.UpdateBoth),
    };

    private static SyncMode _lastMode = SyncMode.Update;

    private readonly FolderCompareResult _comparison;
    private readonly ComboBox _mode = new() { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _summary = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListView _list = new() { MinHeight = 240 };
    private readonly Button _sync;

    public SyncDialog(FolderCompareResult comparison)
    {
        _comparison = comparison;
        Title = "DiffRapid";
        Width = 880;
        Height = 560;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        HasMinimizeButton = false;
        HasMaximizeButton = false;
        SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
        SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

        var heading = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
        heading.Children.Add(new TextBlock { Text = "Synchronize folders", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock
        {
            Text = $"{comparison.LeftRoot}  ↔  {comparison.RightRoot}. Review the operations below; deleted files go to the Recycle Bin.",
            Margin = new Thickness(0, 4, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(subtitle);
        _mode.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
        foreach (var (label, _, _) in Modes)
        {
            _mode.Items.Add(label);
        }

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var modeLabel = new Label { Content = "_Profile:", Target = _mode, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        modeLabel.SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);
        modeRow.Children.Add(modeLabel);
        modeRow.Children.Add(_mode);
        heading.Children.Add(modeRow);
        _description.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(_description);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Operation", Width = 190, CellTemplate = OperationTemplate() });
        view.Columns.Add(new GridViewColumn { Header = "Path", Width = 360, DisplayMemberBinding = new Binding(nameof(OperationRow.Path)) });
        view.Columns.Add(new GridViewColumn { Header = "Reason", Width = 200, DisplayMemberBinding = new Binding(nameof(OperationRow.Reason)) });
        _list.View = view;
        _list.Margin = new Thickness(20, 0, 20, 12);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogListViewStyleKey);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, VsResourceKeys.ThemedDialogListViewItemGridStyleKey);
        _list.Loaded += (_, _) => view.ColumnHeaderContainerStyle = _list.TryFindResource(VsResourceKeys.ThemedDialogGridViewColumnHeaderStyleKey) as Style;

        _sync = DialogButton("_Synchronize", (_, _) => DialogResult = true);
        _sync.IsDefault = true;
        var cancel = DialogButton("Cancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_sync);
        buttons.Children.Add(cancel);
        var footerRow = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footerRow.Children.Add(buttons);
        footerRow.Children.Add(_summary);
        var footer = new Border { Child = footerRow, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        footer.SetResourceReference(Border.BorderBrushProperty, ThemedDialogColors.WindowBorderBrushKey);

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        layout.Children.Add(_list);
        Content = layout;

        _mode.SelectionChanged += (_, _) => Replan();
        _mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == _lastMode));
    }

    /// <summary>The plan shown when the user chose Synchronize.</summary>
    public SyncPlan? Plan { get; private set; }

    private void Replan()
    {
        var (_, description, mode) = Modes[_mode.SelectedIndex];
        _lastMode = mode;
        _description.Text = description;
        Plan = SyncPlanner.Plan(_comparison, new SyncProfile { Mode = mode });

        // Skips are noise in the list unless they explain something; conflicts and changes come first.
        var rows = Plan.Operations
            .Where(o => o.Action != SyncAction.Skip || o.Reason is not null)
            .OrderBy(o => o.Action == SyncAction.Conflict ? 0 : o.ChangesFiles ? 1 : 2)
            .Select(o => new OperationRow(o))
            .ToList();
        _list.ItemsSource = rows;

        var changes = Plan.Operations.Count(o => o.ChangesFiles);
        var deletes = Plan.Count(SyncAction.Delete);
        var conflicts = Plan.Count(SyncAction.Conflict);
        _summary.Text = changes == 0
            ? "Nothing to do: the folders already agree for this profile."
            : Counted(changes, "operation", "operations") + ": " + string.Join(", ", new[]
                {
                    (Plan.Count(SyncAction.Copy), "copy", "copies"),
                    (Plan.Count(SyncAction.Overwrite), "overwrite", "overwrites"),
                    (deletes, "deletion", "deletions"),
                }.Where(p => p.Item1 > 0).Select(p => Counted(p.Item1, p.Item2, p.Item3)))
              + (conflicts > 0 ? "; " + Counted(conflicts, "conflict is", "conflicts are") + " left alone" : string.Empty);
        _sync.IsEnabled = changes > 0;
    }

    private static Button DialogButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
        button.Click += onClick;
        return button;
    }

    private static string Counted(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    // The operation in words, coloured like the folder view: deletions red, conflicts amber.
    private static DataTemplate OperationTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(OperationRow.Action)));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(OperationRow.ActionBrush)));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        return new DataTemplate { VisualTree = text };
    }

    private sealed class OperationRow
    {
        public OperationRow(SyncOperation operation)
        {
            // The side that is written: the target of the operation.
            var side = operation.Direction == SyncDirection.RightToLeft ? "left" : "right";
            Action = operation.Action switch
            {
                SyncAction.Copy => $"Copy to the {side}",
                SyncAction.Overwrite => $"Overwrite on the {side}",
                SyncAction.Delete => $"Delete on the {side}",
                SyncAction.CreateFolder => $"Create folder on the {side}",
                SyncAction.Conflict => "Conflict: left alone",
                _ => "Skip",
            };
            ActionBrush = operation.Action switch
            {
                SyncAction.Delete => DiffTheme.RemovedBrush,
                SyncAction.Conflict => DiffTheme.ChangedBrush,
                SyncAction.Skip => DiffTheme.IgnoredBrush,
                _ => DiffTheme.AddedBrush,
            };
            Path = operation.RelativePath;
            Reason = operation.Reason ?? string.Empty;
        }

        public string Action { get; }

        public Brush ActionBrush { get; }

        public string Path { get; }

        public string Reason { get; }
    }
}
