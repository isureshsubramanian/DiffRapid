using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace DiffRapid.VisualStudio.Editor;

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("any")]
[TextViewRole(DiffRapidRoles.Pane)]
internal sealed class DiffHighlighterProvider : IWpfTextViewCreationListener
{
    public const string LayerName = "DiffRapid.Background";

    [Export(typeof(AdornmentLayerDefinition))]
    [Name(LayerName)]
    [Order(Before = PredefinedAdornmentLayers.Selection)]
    public AdornmentLayerDefinition? Layer { get; set; }

    [Import]
    public IEditorFormatMapService FormatMapService { get; set; } = null!;

    public void TextViewCreated(IWpfTextView textView)
    {
        _ = new DiffHighlighter(textView, FormatMapService.GetEditorFormatMap(textView));
    }
}

/// <summary>Paints line backgrounds, filler areas and intra-line changes behind the text.</summary>
internal sealed class DiffHighlighter
{
    private const double AccentWidth = 3;
    private const double AccentStrength = 0.9;

    private readonly IWpfTextView _view;
    private readonly IAdornmentLayer _layer;
    private readonly IEditorFormatMap _formatMap;
    private readonly Dictionary<string, Brush> _brushes = new();

    public DiffHighlighter(IWpfTextView view, IEditorFormatMap formatMap)
    {
        _view = view;
        _formatMap = formatMap;
        _layer = view.GetAdornmentLayer(DiffHighlighterProvider.LayerName);

        _view.LayoutChanged += (_, _) => Redraw();
        _view.ViewportWidthChanged += (_, _) => Redraw();
        _formatMap.FormatMappingChanged += (_, _) =>
        {
            _brushes.Clear();
            Redraw();
        };
        _view.Closed += (_, _) => _layer.RemoveAllAdornments();
    }

    private void Redraw()
    {
        if (_view.IsClosed || _view.InLayout)
        {
            return;
        }

        _layer.RemoveAllAdornments();
        var state = DiffPaneState.Get(_view);
        if (state is null)
        {
            return;
        }

        var left = _view.ViewportLeft;
        var width = System.Math.Max(_view.ViewportWidth, 1);
        foreach (var line in _view.TextViewLines)
        {
            var lineNumber = line.Start.GetContainingLine().LineNumber;

            if (line.TextTop > line.Top + 0.5)
            {
                Add(new Rect(left, line.Top, width, line.TextTop - line.Top), Brush(DiffFormatNames.Filler));
            }

            if (line.Bottom > line.TextBottom + 0.5)
            {
                Add(new Rect(left, line.TextBottom, width, line.Bottom - line.TextBottom), Brush(DiffFormatNames.Filler));
            }

            if (lineNumber >= state.LineCount)
            {
                continue;
            }

            var format = state.LineKind[lineNumber] switch
            {
                PaneLineKind.Changed => DiffFormatNames.ChangedLine,
                PaneLineKind.Inserted => state.IsLeft ? DiffFormatNames.RemovedLine : DiffFormatNames.InsertedLine,
                PaneLineKind.Moved => DiffFormatNames.MovedLine,
                PaneLineKind.Ignored => DiffFormatNames.IgnoredLine,
                PaneLineKind.Conflict => DiffFormatNames.ConflictLine,
                PaneLineKind.Resolved => DiffFormatNames.ResolvedLine,
                _ => null,
            };

            if (format is null)
            {
                continue;
            }

            Add(new Rect(left, line.TextTop, width, line.TextHeight), Brush(format));
            Add(new Rect(left, line.TextTop, AccentWidth, line.TextHeight), Brush(format, AccentStrength));
            DrawInline(line, state, lineNumber);
        }
    }

    private void DrawInline(ITextViewLine line, DiffPaneState state, int lineNumber)
    {
        var spans = state.InlineSpans(lineNumber);
        if (spans.Count == 0)
        {
            return;
        }

        var snapshotLine = line.Start.GetContainingLine();
        foreach (var span in spans)
        {
            var start = snapshotLine.Start.Position + span.Start;
            var end = System.Math.Min(snapshotLine.End.Position, start + span.Length);
            if (end <= start || start < line.Start.Position || start >= line.EndIncludingLineBreak.Position)
            {
                continue;
            }

            var geometry = _view.TextViewLines.GetMarkerGeometry(new SnapshotSpan(_view.TextSnapshot, Span.FromBounds(start, end)));
            if (geometry is null)
            {
                continue;
            }

            var path = new Path { Data = geometry, Fill = Brush(DiffFormatNames.ChangedText) };
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, path, null);
        }
    }

    private void Add(Rect rect, Brush brush)
    {
        var shape = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = brush };
        Canvas.SetLeft(shape, rect.Left);
        Canvas.SetTop(shape, rect.Top);
        _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, shape, null);
    }

    private Brush Brush(string formatName, double? strength = null)
    {
        var key = strength is null ? formatName : formatName + "#" + strength;
        if (_brushes.TryGetValue(key, out var brush))
        {
            return brush;
        }

        // Fonts and Colors stores opaque colours, so the user picks the hue and the strength is fixed here;
        // translucent tints keep the editor's syntax colouring readable on light and dark themes.
        var properties = _formatMap.GetProperties(formatName);
        var color = properties[EditorFormatDefinition.BackgroundColorId] is Color c
            ? c
            : (properties[EditorFormatDefinition.BackgroundBrushId] as SolidColorBrush)?.Color ?? Colors.Gray;
        color.A = 0xFF;

        brush = formatName == DiffFormatNames.Filler && strength is null
            ? Hatch(color)
            : new SolidColorBrush(color) { Opacity = strength ?? TintStrength(formatName) };
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    private static double TintStrength(string formatName) => formatName switch
    {
        DiffFormatNames.ChangedText => 0.38,
        DiffFormatNames.IgnoredLine => 0.10,
        _ => 0.16,
    };

    // Diagonal stripes for rows that exist only on the other side.
    private static Brush Hatch(Color color)
    {
        var faint = Color.FromArgb(0x10, color.R, color.G, color.B);
        var strong = Color.FromArgb(0x30, color.R, color.G, color.B);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(6, 6),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        brush.GradientStops.Add(new GradientStop(strong, 0));
        brush.GradientStops.Add(new GradientStop(strong, 0.3));
        brush.GradientStops.Add(new GradientStop(faint, 0.3));
        brush.GradientStops.Add(new GradientStop(faint, 1));
        return brush;
    }
}
