using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Title block above each editor: side badge, file name, folder and line count.</summary>
internal sealed class FileHeader : Border
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
        ToolTip = "This file has changes that are not saved yet (Ctrl+S saves)",
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

    /// <summary>Shows a "Read-only" mark for a pane that holds text taken from <paramref name="source"/>.</summary>
    public void SetReadOnly(string source)
    {
        var mark = new TextBlock
        {
            Text = "Read-only",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = $"Text taken from {source}. It is compared, not edited: changes here would not reach the original file.",
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
        DockPanel.SetDock(mark, Dock.Right);
        var row = (DockPanel)Child;
        row.Children.Insert(row.Children.IndexOf(_modified) + 1, mark);
    }
}
