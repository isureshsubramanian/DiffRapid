using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Core.Comparison;
using DiffRapid.VisualStudio.Editor;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// The location pane: a miniature map of all differences with the visible area outlined.
/// Clicking it scrolls to that position.
/// </summary>
internal sealed class DiffOverviewBar : FrameworkElement
{
    private static readonly Brush ChangedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x00)));
    private static readonly Brush RemovedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x4B, 0x4B)));
    private static readonly Brush AddedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
    private static readonly Brush IgnoredBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)));
    private static readonly Pen ViewportPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0x80, 0x80)), 1));

    private DiffModel? _model;
    private double _viewportTopRow;
    private double _viewportRows;

    public event EventHandler<int>? RowRequested;

    public DiffOverviewBar()
    {
        Width = 14;
        Cursor = Cursors.Hand;
        ToolTip = "Differences overview: click to jump";
    }

    public void SetModel(DiffModel? model)
    {
        _model = model;
        InvalidateVisual();
    }

    public void SetViewport(double topRow, double rows)
    {
        _viewportTopRow = topRow;
        _viewportRows = rows;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var model = _model;
        if (model is null || model.Rows.Count == 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = ActualHeight / model.Rows.Count;
        var half = ActualWidth / 2;
        for (var r = 0; r < model.Rows.Count; r++)
        {
            var row = model.Rows[r];
            if (row.Kind == AlignedRowKind.Equal)
            {
                continue;
            }

            var y = r * scale;
            var h = Math.Max(scale, 2);
            switch (row.Kind)
            {
                case AlignedRowKind.Changed:
                    dc.DrawRectangle(ChangedBrush, null, new Rect(0, y, ActualWidth, h));
                    break;
                case AlignedRowKind.Removed:
                    dc.DrawRectangle(RemovedBrush, null, new Rect(0, y, half, h));
                    break;
                case AlignedRowKind.Added:
                    dc.DrawRectangle(AddedBrush, null, new Rect(half, y, half, h));
                    break;
                default:
                    dc.DrawRectangle(IgnoredBrush, null, new Rect(0, y, ActualWidth, h));
                    break;
            }
        }

        var top = _viewportTopRow * scale;
        var height = Math.Max(_viewportRows * scale, 4);
        dc.DrawRectangle(null, ViewportPen, new Rect(0.5, top, Math.Max(ActualWidth - 1, 1), height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_model is { Rows.Count: > 0 } model && ActualHeight > 0)
        {
            var row = (int)(e.GetPosition(this).Y / ActualHeight * model.Rows.Count);
            RowRequested?.Invoke(this, Math.Max(0, Math.Min(model.Rows.Count - 1, row)));
            e.Handled = true;
        }
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
