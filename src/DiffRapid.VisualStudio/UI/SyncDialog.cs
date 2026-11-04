using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        modeRow.Children.Add(new Label { Content = "_Profile:", Target = _mode, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        modeRow.Children.Add(_mode);
        heading.Children.Add(modeRow);
        _description.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(_description);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Operation", Width = 110, DisplayMemberBinding = new Binding(nameof(OperationRow.Action)) });
        view.Columns.Add(new GridViewColumn { Header = "Direction", Width = 80, DisplayMemberBinding = new Binding(nameof(OperationRow.Direction)) });
        view.Columns.Add(new GridViewColumn { Header = "Path", Width = 400, DisplayMemberBinding = new Binding(nameof(OperationRow.Path)) });
        view.Columns.Add(new GridViewColumn { Header = "Reason", Width = 230, DisplayMemberBinding = new Binding(nameof(OperationRow.Reason)) });
        _list.View = view;
        _list.Margin = new Thickness(20, 0, 20, 12);
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
            : $"{changes} operations: {Plan.Count(SyncAction.Copy)} copies, {Plan.Count(SyncAction.Overwrite)} overwrites, {deletes} deletions"
              + (conflicts > 0 ? $"; {conflicts} conflicts are left alone" : string.Empty);
        _sync.IsEnabled = changes > 0;
    }

    private static Button DialogButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
        button.Click += onClick;
        return button;
    }

    private sealed class OperationRow
    {
        public OperationRow(SyncOperation operation)
        {
            Action = operation.Action switch
            {
                SyncAction.Copy => "Copy",
                SyncAction.Overwrite => "Overwrite",
                SyncAction.Delete => "Delete",
                SyncAction.CreateFolder => "Create folder",
                SyncAction.Conflict => "Conflict",
                _ => "Skip",
            };
            Direction = operation.Direction switch
            {
                SyncDirection.LeftToRight => "→",
                SyncDirection.RightToLeft => "←",
                _ => string.Empty,
            };
            Path = operation.RelativePath;
            Reason = operation.Reason ?? string.Empty;
        }

        public string Action { get; }

        public string Direction { get; }

        public string Path { get; }

        public string Reason { get; }
    }
}
