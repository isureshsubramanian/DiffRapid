using System;
using System.Collections.Generic;

namespace DiffRapid.Core.Merge;

/// <summary>A three-pane row; null line numbers identify filler cells.</summary>
public sealed record MergeRow(int ChunkIndex, MergeChunkKind Kind, int? BaseLine, int? LeftLine, int? RightLine);

/// <summary>Builds an aligned three-pane layout from merge chunks.</summary>
public static class MergeAlignment
{
    /// <summary>Pairs source lines by their index within each chunk.</summary>
    public static IReadOnlyList<MergeRow> Build(MergeResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        var rows = new List<MergeRow>();
        for (var i = 0; i < result.Chunks.Count; i++)
        {
            var c = result.Chunks[i];
            var count = Math.Max(c.BaseEnd - c.BaseStart, Math.Max(c.LeftEnd - c.LeftStart, c.RightEnd - c.RightStart));
            for (var k = 0; k < count; k++)
            {
                rows.Add(new MergeRow(i, c.Kind,
                    c.BaseStart + k < c.BaseEnd ? c.BaseStart + k : (int?)null,
                    c.LeftStart + k < c.LeftEnd ? c.LeftStart + k : (int?)null,
                    c.RightStart + k < c.RightEnd ? c.RightStart + k : (int?)null));
            }
        }

        return rows;
    }
}
