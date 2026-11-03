using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiffRapid.Core.Comparison;
using DiffRapid.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// The location pane: a miniature map of all differences with the visible area outlined.
/// Clicking or dragging on it scrolls to that position.
/// </summary>
internal sealed class DiffOverviewBar : FrameworkElement
{
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(DiffOverviewBar), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LensBrushProperty = DependencyProperty.Register(
        nameof(LensBrush), typeof(Brush), typeof(DiffOverviewBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Inset = 3;

    private DiffModel? _model;
    private double _viewportTopRow;
    private double _viewportRows;

    public event EventHandler<int>? RowRequested;

    public DiffOverviewBar()
    {
        Width = 18;
        Cursor = Cursors.Hand;
        ToolTip = "Differences overview: click or drag to jump";
        SetResourceReference(TrackBrushProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
        SetResourceReference(LensBrushProperty, EnvironmentColors.ScrollBarThumbBackgroundBrushKey);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush LensBrush
    {
        get => (Brush)GetValue(LensBrushProperty);
        set => SetValue(LensBrushProperty, value);
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
        dc.DrawRectangle(TrackBrush, null, new Rect(RenderSize));
        var model = _model;
        if (model is null || model.Rows.Count == 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = ActualHeight / Math.Max(model.Rows.Count, 1);

        // Viewport lens first, so the difference marks stay readable on top of it.
        var lensTop = _viewportTopRow * scale;
        var lensHeight = Math.Max(_viewportRows * scale, 6);
        dc.PushOpacity(0.45);
        dc.DrawRoundedRectangle(LensBrush, null, new Rect(1, lensTop, Math.Max(ActualWidth - 2, 1), lensHeight), 3, 3);
        dc.Pop();

        var width = ActualWidth - (2 * Inset);
        var half = width / 2;
        var r = 0;
        while (r < model.Rows.Count)
        {
            var kind = model.Rows[r].Kind;
            var start = r;
            while (r < model.Rows.Count && model.Rows[r].Kind == kind)
            {
                r++;
            }

            if (kind == AlignedRowKind.Equal)
            {
                continue;
            }

            // One rounded mark per run of rows of the same kind.
            var y = start * scale;
            var h = Math.Max((r - start) * scale, 3);
            var (brush, x, w) = kind switch
            {
                AlignedRowKind.Changed => (DiffTheme.ChangedBrush, Inset, width),
                AlignedRowKind.Removed => (DiffTheme.RemovedBrush, Inset, half - 0.5),
                AlignedRowKind.Added => (DiffTheme.AddedBrush, Inset + half + 0.5, half - 0.5),
                _ => (DiffTheme.IgnoredBrush, Inset, width),
            };
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, Math.Max(w, 1), h), 1.5, 1.5);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (RequestRowAt(e.GetPosition(this).Y))
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            RequestRowAt(e.GetPosition(this).Y);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private bool RequestRowAt(double y)
    {
        if (_model is not { Rows.Count: > 0 } model || ActualHeight <= 0)
        {
            return false;
        }

        var row = (int)(y / ActualHeight * model.Rows.Count);
        RowRequested?.Invoke(this, Math.Max(0, Math.Min(model.Rows.Count - 1, row)));
        return true;
    }
}
