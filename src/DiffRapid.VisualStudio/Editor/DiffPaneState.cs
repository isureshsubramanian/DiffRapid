using System;
using System.Collections.Generic;
using DiffRapid.Core.Comparison;
using Microsoft.VisualStudio.Text.Editor;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>Text view role that marks the editor views hosted in a DiffRapid comparison.</summary>
internal static class DiffRapidRoles
{
    public const string Pane = "DIFFRAPID_PANE";
}

/// <summary>How a line of one pane is displayed.</summary>
internal enum PaneLineKind : byte
{
    Equal,
    Changed,
    Inserted,
    Ignored,
    Moved,
}

/// <summary>
/// Per-pane layout data computed from a comparison: which lines are highlighted, where filler rows go
/// and which characters changed inside a line. Stored in the text view's property bag.
/// </summary>
internal sealed class DiffPaneState
{
    private static readonly IReadOnlyList<TextSpan> NoSpans = Array.Empty<TextSpan>();

    public DiffPaneState(int lineCount)
    {
        LineCount = lineCount;
        FillerBefore = new int[lineCount + 1];
        RowOfLine = new int[lineCount];
        LineKind = new PaneLineKind[lineCount];
        Inline = new IReadOnlyList<TextSpan>?[lineCount];
    }

    /// <summary>Number of lines in the compared text (excludes the editor's empty line after a final newline).</summary>
    public int LineCount { get; }

    /// <summary>Filler rows shown before line k; index LineCount holds the rows after the last line.</summary>
    public int[] FillerBefore { get; }

    /// <summary>Aligned row index of each line.</summary>
    public int[] RowOfLine { get; }

    public PaneLineKind[] LineKind { get; }

    public IReadOnlyList<TextSpan>?[] Inline { get; }

    public static DiffPaneState? Get(ITextView view)
    {
        return view.Properties.TryGetProperty(typeof(DiffPaneState), out DiffPaneState state) ? state : null;
    }

    public static void Set(ITextView view, DiffPaneState state)
    {
        view.Properties[typeof(DiffPaneState)] = state;
    }

    public IReadOnlyList<TextSpan> InlineSpans(int line) =>
        line >= 0 && line < LineCount ? Inline[line] ?? NoSpans : NoSpans;

    /// <summary>Row at which line k's block (its filler rows plus the line) starts.</summary>
    public int BlockTopRow(int line) => RowOfLine[line] - FillerBefore[line];

    /// <summary>First line whose row is at or after <paramref name="row"/>, or the last line.</summary>
    public int LineAtOrAfterRow(int row)
    {
        int lo = 0, hi = LineCount - 1;
        if (hi < 0)
        {
            return 0;
        }

        if (RowOfLine[hi] < row)
        {
            return hi;
        }

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (RowOfLine[mid] < row)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
