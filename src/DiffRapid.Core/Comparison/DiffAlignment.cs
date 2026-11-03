using System.Collections.Generic;

namespace DiffRapid.Core.Comparison;

public enum AlignedRowKind
{
    Equal,
    Changed,
    Added,
    Removed,
    Ignored,
}

/// <summary>
/// One visual row of a side-by-side view. A null line number means the side shows a filler (padding) line.
/// </summary>
public readonly record struct AlignedRow(int? LeftLine, int? RightLine, AlignedRowKind Kind, int HunkIndex);

/// <summary>Builds the row layout that keeps both panes of a side-by-side view aligned.</summary>
public static class DiffAlignment
{
    public static IReadOnlyList<AlignedRow> Build(TextDiffResult result)
    {
        var rows = new List<AlignedRow>(System.Math.Max(result.Left.LineCount, result.Right.LineCount));
        int left = 0, right = 0;
        for (var h = 0; h < result.Hunks.Count; h++)
        {
            var hunk = result.Hunks[h];
            while (left < hunk.LeftStart)
            {
                rows.Add(new AlignedRow(left++, right++, AlignedRowKind.Equal, -1));
            }

            var ignored = hunk.Significance == DiffSignificance.Ignored;
            var paired = System.Math.Min(hunk.LeftCount, hunk.RightCount);
            for (var k = 0; k < paired; k++)
            {
                rows.Add(new AlignedRow(left++, right++, ignored ? AlignedRowKind.Ignored : AlignedRowKind.Changed, h));
            }

            for (var k = paired; k < hunk.LeftCount; k++)
            {
                rows.Add(new AlignedRow(left++, null, ignored ? AlignedRowKind.Ignored : AlignedRowKind.Removed, h));
            }

            for (var k = paired; k < hunk.RightCount; k++)
            {
                rows.Add(new AlignedRow(null, right++, ignored ? AlignedRowKind.Ignored : AlignedRowKind.Added, h));
            }
        }

        while (left < result.Left.LineCount && right < result.Right.LineCount)
        {
            rows.Add(new AlignedRow(left++, right++, AlignedRowKind.Equal, -1));
        }

        return rows;
    }
}
