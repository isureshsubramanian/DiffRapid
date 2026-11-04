using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DiffRapid.Core.VersionControl;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Lists the versions of a file from git or TFVC, newest first, to pick one to compare with.</summary>
internal sealed class VersionPickerDialog : DialogWindow
{
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Single, MinHeight = 260 };
    private readonly Button _compare;

    /// <param name="authorHeader">The Author column's title; the local history shows how a version was kept there.</param>
    /// <param name="descriptionHeader">The Description column's title; the local history shows the size there.</param>
    public VersionPickerDialog(
        string fileName,
        string provider,
        IReadOnlyList<VersionInfo> versions,
        string authorHeader = "Author",
        string descriptionHeader = "Description")
    {
        Title = "DiffRapid";
        Width = 760;
        Height = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        HasMinimizeButton = false;
        HasMaximizeButton = false;
        SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
        SetResourceReference(ForegroundProperty, ThemedDialogColors.WindowPanelTextBrushKey);

        var heading = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
        heading.Children.Add(new TextBlock { Text = "Compare with an earlier version", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock
        {
            Text = $"Versions of {fileName} in {provider}, newest first. The chosen version opens on the left, your working copy on the right.",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        heading.Children.Add(subtitle);

        var view = new GridView();
        view.Columns.Add(Column("Version", nameof(VersionInfo.ShortId), 90));
        view.Columns.Add(Column("Date", nameof(VersionInfo.Date), 140, "{0:yyyy-MM-dd HH:mm}"));
        view.Columns.Add(Column(authorHeader, nameof(VersionInfo.Author), 140));
        view.Columns.Add(Column(descriptionHeader, nameof(VersionInfo.Subject), 340));
        _list.View = view;

        // The Visual Studio list styles keep the selected row readable in every theme.
        _list.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogListViewStyleKey);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, VsResourceKeys.ThemedDialogListViewItemGridStyleKey);
        DiffTheme.ShowColumnHeaders(_list, view);
        _list.ItemsSource = versions;
        _list.Margin = new Thickness(20, 0, 20, 12);
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.SelectedItem is not null && e.ChangedButton == MouseButton.Left)
            {
                DialogResult = true;
            }
        };
        _list.SelectionChanged += (_, _) => _compare!.IsEnabled = _list.SelectedItem is not null;

        _compare = DialogButton("_Compare", (_, _) => DialogResult = true);
        _compare.IsDefault = true;
        var cancel = DialogButton("Cancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_compare);
        buttons.Children.Add(cancel);
        var footer = new Border { Child = buttons, Padding = new Thickness(20, 12, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarGradientBeginBrushKey);
        footer.SetResourceReference(Border.BorderBrushProperty, ThemedDialogColors.WindowBorderBrushKey);

        var layout = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(footer);
        layout.Children.Add(_list);
        Content = layout;

        _list.SelectedIndex = versions.Count > 0 ? 0 : -1;
        _compare.IsEnabled = versions.Count > 0;
        Loaded += (_, _) => _list.Focus();
    }

    public VersionInfo? SelectedVersion => _list.SelectedItem as VersionInfo;

    private static GridViewColumn Column(string header, string property, double width, string? format = null) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(property) { StringFormat = format, ConverterCulture = CultureInfo.CurrentCulture },
    };

    private static Button DialogButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        button.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
        button.Click += onClick;
        return button;
    }
}
