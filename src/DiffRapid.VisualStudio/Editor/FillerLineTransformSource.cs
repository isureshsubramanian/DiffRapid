using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>
/// Adds vertical space above lines so that both panes stay aligned row by row. The space is the
/// "filler" area where the other side has lines this side does not.
/// </summary>
[Export(typeof(ILineTransformSourceProvider))]
[ContentType("any")]
[TextViewRole(DiffRapidRoles.Pane)]
internal sealed class FillerLineTransformSourceProvider : ILineTransformSourceProvider
{
    public ILineTransformSource Create(IWpfTextView textView) => new FillerLineTransformSource(textView);
}

internal sealed class FillerLineTransformSource : ILineTransformSource
{
    private readonly IWpfTextView _view;

    public FillerLineTransformSource(IWpfTextView view)
    {
        _view = view;
    }

    public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
    {
        var transform = line.DefaultLineTransform;
        var state = DiffPaneState.Get(_view);
        if (state is null)
        {
            return transform;
        }

        var lineNumber = line.Start.GetContainingLine().LineNumber;
        var height = _view.LineHeight;
        double top = 0, bottom = 0;

        if (line.IsFirstTextViewLineForSnapshotLine && lineNumber < state.LineCount)
        {
            top = state.FillerBefore[lineNumber] * height;
        }
        else if (line.IsFirstTextViewLineForSnapshotLine && state.LineCount == 0 && lineNumber == 0)
        {
            top = state.FillerBefore[0] * height;
        }

        if (line.IsLastTextViewLineForSnapshotLine && state.LineCount > 0 && lineNumber == state.LineCount - 1)
        {
            bottom = state.FillerBefore[state.LineCount] * height;
        }

        if (top == 0 && bottom == 0)
        {
            return transform;
        }

        return new LineTransform(transform.TopSpace + top, transform.BottomSpace + bottom, transform.VerticalScale, transform.Right);
    }
}
