using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>
/// A computed three-way merge laid out for the merge view: the left, base and right panes aligned row by
/// row, and the line colours of every pane for the current choices. Built off the UI thread.
/// </summary>
internal sealed class MergeLayout
{
    private MergeLayout(MergeSession session, IReadOnlyList<MergeRow> rows, DiffPaneState left, DiffPaneState @base, DiffPaneState right, int[] chunkFirstRow)
    {
        Session = session;
        Rows = rows;
        Left = left;
        Base = @base;
        Right = right;
        ChunkFirstRow = chunkFirstRow;
    }

    public MergeSession Session { get; }

    public IReadOnlyList<MergeRow> Rows { get; }

    public DiffPaneState Left { get; }

    public DiffPaneState Base { get; }

    public DiffPaneState Right { get; }

    /// <summary>First aligned row of each chunk.</summary>
    public int[] ChunkFirstRow { get; }

    public MergeResult Result => Session.Result;

    public static MergeLayout Build(string baseText, string leftText, string rightText, CompareOptions options, CancellationToken cancellationToken)
    {
        var baseDoc = TextDocument.Parse(baseText);
        var leftDoc = TextDocument.Parse(leftText);
        var rightDoc = TextDocument.Parse(rightText);
        var result = ThreeWayMerger.Merge(baseDoc, leftDoc, rightDoc, options, cancellationToken);
        var rows = MergeAlignment.Build(result);

        // isLeft: false everywhere, so "inserted" lines read as green: lines that go into the result.
        var left = new DiffPaneState(leftDoc.LineCount, isLeft: false);
        var @base = new DiffPaneState(baseDoc.LineCount, isLeft: false);
        var right = new DiffPaneState(rightDoc.LineCount, isLeft: false);
        var chunkFirstRow = new int[result.Chunks.Count];
        for (var i = 0; i < chunkFirstRow.Length; i++)
        {
            chunkFirstRow[i] = -1;
        }

        int leftFillers = 0, baseFillers = 0, rightFillers = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[r];
            if (chunkFirstRow[row.ChunkIndex] < 0)
            {
                chunkFirstRow[row.ChunkIndex] = r;
            }

            Place(left, row.LeftLine, r, ref leftFillers);
            Place(@base, row.BaseLine, r, ref baseFillers);
            Place(right, row.RightLine, r, ref rightFillers);
        }

        left.FillerBefore[left.LineCount] = leftFillers;
        @base.FillerBefore[@base.LineCount] = baseFillers;
        right.FillerBefore[right.LineCount] = rightFillers;

        // A chunk without rows (none expected) sits where the next one starts.
        for (var i = chunkFirstRow.Length - 1; i >= 0; i--)
        {
            if (chunkFirstRow[i] < 0)
            {
                chunkFirstRow[i] = i + 1 < chunkFirstRow.Length ? chunkFirstRow[i + 1] : rows.Count;
            }
        }

        var layout = new MergeLayout(new MergeSession(result), rows, left, @base, right, chunkFirstRow);
        layout.UpdateLineKinds();
        return layout;
    }

    /// <summary>The chunk shown on aligned row <paramref name="row"/>.</summary>
    public int ChunkAtRow(int row) => Rows.Count == 0 ? -1 : Rows[Math.Max(0, Math.Min(row, Rows.Count - 1))].ChunkIndex;

    /// <summary>
    /// Colours the three source panes for the current choices. Green marks lines that go into the result
    /// and differ from the base, amber the base lines they replace, red an unresolved conflict and violet
    /// the parts of a resolved conflict that were not taken.
    /// </summary>
    public void UpdateLineKinds()
    {
        for (var i = 0; i < Result.Chunks.Count; i++)
        {
            var c = Result.Chunks[i];
            PaneLineKind left, @base, right;
            switch (c.Kind)
            {
                case MergeChunkKind.LeftOnly:
                    (left, @base, right) = (PaneLineKind.Inserted, PaneLineKind.Changed, PaneLineKind.Equal);
                    break;
                case MergeChunkKind.RightOnly:
                    (left, @base, right) = (PaneLineKind.Equal, PaneLineKind.Changed, PaneLineKind.Inserted);
                    break;
                case MergeChunkKind.BothSame:
                    (left, @base, right) = (PaneLineKind.Inserted, PaneLineKind.Changed, PaneLineKind.Inserted);
                    break;
                case MergeChunkKind.Conflict:
                    var resolution = Session.Resolution(i);
                    if (resolution == MergeResolution.Unresolved)
                    {
                        (left, @base, right) = (PaneLineKind.Conflict, PaneLineKind.Conflict, PaneLineKind.Conflict);
                    }
                    else
                    {
                        var takesLeft = resolution is MergeResolution.Left or MergeResolution.LeftThenRight or MergeResolution.RightThenLeft;
                        var takesRight = resolution is MergeResolution.Right or MergeResolution.LeftThenRight or MergeResolution.RightThenLeft;
                        left = takesLeft ? PaneLineKind.Inserted : PaneLineKind.Resolved;
                        right = takesRight ? PaneLineKind.Inserted : PaneLineKind.Resolved;
                        @base = resolution == MergeResolution.Base ? PaneLineKind.Inserted : PaneLineKind.Resolved;
                    }

                    break;
                default:
                    (left, @base, right) = (PaneLineKind.Equal, PaneLineKind.Equal, PaneLineKind.Equal);
                    break;
            }

            Fill(Left, c.LeftStart, c.LeftEnd, left);
            Fill(Base, c.BaseStart, c.BaseEnd, @base);
            Fill(Right, c.RightStart, c.RightEnd, right);
        }
    }

    /// <summary>Layout of the merged text: one row per line, coloured by the chunk each line came from.</summary>
    public DiffPaneState ResultState(int lineCount, IReadOnlyList<(int Start, int Count)> ranges)
    {
        var state = new DiffPaneState(lineCount, isLeft: false);
        for (var line = 0; line < lineCount; line++)
        {
            state.RowOfLine[line] = line;
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var kind = Session.Resolution(i) == MergeResolution.Custom ? PaneLineKind.Resolved : Result.Chunks[i].Kind switch
            {
                MergeChunkKind.Unchanged => PaneLineKind.Equal,
                MergeChunkKind.Conflict => Session.Resolution(i) == MergeResolution.Unresolved ? PaneLineKind.Conflict : PaneLineKind.Resolved,
                _ => PaneLineKind.Inserted,
            };
            Fill(state, ranges[i].Start, ranges[i].Start + ranges[i].Count, kind);
        }

        return state;
    }

    private static void Place(DiffPaneState state, int? line, int row, ref int fillers)
    {
        if (line is int l)
        {
            state.FillerBefore[l] = fillers;
            state.RowOfLine[l] = row;
            fillers = 0;
        }
        else
        {
            fillers++;
        }
    }

    private static void Fill(DiffPaneState state, int start, int end, PaneLineKind kind)
    {
        for (var line = Math.Max(0, start); line < Math.Min(end, state.LineCount); line++)
        {
            state.LineKind[line] = kind;
        }
    }
}
