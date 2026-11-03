using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>A computed two-way comparison, ready to be applied to the panes. Built off the UI thread.</summary>
internal sealed class DiffModel
{
    private const int MaxInlineLineLength = 4000;

    private DiffModel(TextDiffResult result, IReadOnlyList<AlignedRow> rows, DiffPaneState left, DiffPaneState right, IReadOnlyList<int> hunkRows)
    {
        Result = result;
        Rows = rows;
        Left = left;
        Right = right;
        HunkRows = hunkRows;
    }

    public TextDiffResult Result { get; }

    public IReadOnlyList<AlignedRow> Rows { get; }

    public DiffPaneState Left { get; }

    public DiffPaneState Right { get; }

    /// <summary>First row of every important difference, in order.</summary>
    public IReadOnlyList<int> HunkRows { get; }

    public static DiffModel Build(string leftText, string rightText, CompareOptions options, CancellationToken cancellationToken)
    {
        var leftDoc = TextDocument.Parse(leftText);
        var rightDoc = TextDocument.Parse(rightText);
        var result = TextComparer.Compare(leftDoc, rightDoc, options, cancellationToken);
        var rows = DiffAlignment.Build(result);

        var left = new DiffPaneState(leftDoc.LineCount, isLeft: true);
        var right = new DiffPaneState(rightDoc.LineCount, isLeft: false);
        var hunkRows = new List<int>();
        int leftFillers = 0, rightFillers = 0, lastHunk = -1;

        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];
            if (row.HunkIndex >= 0 && row.HunkIndex != lastHunk)
            {
                lastHunk = row.HunkIndex;
                if (result.Hunks[row.HunkIndex].Significance == DiffSignificance.Important)
                {
                    hunkRows.Add(r);
                }
            }

            var kind = row.Kind switch
            {
                AlignedRowKind.Equal => PaneLineKind.Equal,
                AlignedRowKind.Ignored => PaneLineKind.Ignored,
                AlignedRowKind.Changed => PaneLineKind.Changed,
                _ => PaneLineKind.Inserted,
            };

            if (row.LeftLine is int l)
            {
                left.FillerBefore[l] = leftFillers;
                left.RowOfLine[l] = r;
                left.LineKind[l] = kind;
                leftFillers = 0;
            }
            else
            {
                leftFillers++;
            }

            if (row.RightLine is int rr)
            {
                right.FillerBefore[rr] = rightFillers;
                right.RowOfLine[rr] = r;
                right.LineKind[rr] = kind;
                rightFillers = 0;
            }
            else
            {
                rightFillers++;
            }

            if (row.Kind == AlignedRowKind.Changed && row.LeftLine is int il && row.RightLine is int ir)
            {
                var a = leftDoc[il].Content;
                var b = rightDoc[ir].Content;
                if (a.Length <= MaxInlineLineLength && b.Length <= MaxInlineLineLength)
                {
                    var inline = InlineDiffer.Compare(a, b, options);
                    left.Inline[il] = inline.Left;
                    right.Inline[ir] = inline.Right;
                }
            }
        }

        left.FillerBefore[left.LineCount] = leftFillers;
        right.FillerBefore[right.LineCount] = rightFillers;

        foreach (var move in result.MovedBlocks)
        {
            for (var k = 0; k < move.LineCount; k++)
            {
                left.LineKind[move.LeftStart + k] = PaneLineKind.Moved;
                right.LineKind[move.RightStart + k] = PaneLineKind.Moved;
            }
        }

        return new DiffModel(result, rows, left, right, hunkRows);
    }
}
