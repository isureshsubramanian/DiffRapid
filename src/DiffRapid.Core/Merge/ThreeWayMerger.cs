using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Merge;

public enum MergeChunkKind
{
    /// <summary>No side changed these base lines.</summary>
    Unchanged,

    /// <summary>Only the left side changed; its version is taken.</summary>
    LeftOnly,

    /// <summary>Only the right side changed; its version is taken.</summary>
    RightOnly,

    /// <summary>Both sides made the identical change.</summary>
    BothSame,

    /// <summary>Both sides changed the same base lines differently.</summary>
    Conflict,
}

/// <summary>A region of a three-way merge. Ranges are zero-based, end-exclusive line ranges.</summary>
public sealed record MergeChunk(
    MergeChunkKind Kind,
    int BaseStart, int BaseEnd,
    int LeftStart, int LeftEnd,
    int RightStart, int RightEnd);

/// <summary>Labels written into conflict markers.</summary>
public sealed record ConflictLabels(string Left = "left", string Base = "base", string Right = "right");

/// <summary>The result of a three-way merge.</summary>
public sealed class MergeResult
{
    internal MergeResult(TextDocument @base, TextDocument left, TextDocument right, IReadOnlyList<MergeChunk> chunks)
    {
        Base = @base;
        Left = left;
        Right = right;
        Chunks = chunks;
        foreach (var c in chunks)
        {
            if (c.Kind == MergeChunkKind.Conflict)
            {
                ConflictCount++;
            }
        }
    }

    public TextDocument Base { get; }

    public TextDocument Left { get; }

    public TextDocument Right { get; }

    public IReadOnlyList<MergeChunk> Chunks { get; }

    public int ConflictCount { get; }

    public bool HasConflicts => ConflictCount > 0;

    /// <summary>
    /// Produces the merged text. Conflicts are written with git-compatible markers
    /// (diff3 style when <paramref name="includeBase"/> is true).
    /// </summary>
    public string ToText(ConflictLabels? labels = null, bool includeBase = false)
    {
        labels ??= new ConflictLabels();
        var newline = DetectNewline();
        var sb = new StringBuilder();
        foreach (var chunk in Chunks)
        {
            switch (chunk.Kind)
            {
                case MergeChunkKind.Unchanged:
                    Append(sb, Base, chunk.BaseStart, chunk.BaseEnd, newline);
                    break;
                case MergeChunkKind.LeftOnly:
                case MergeChunkKind.BothSame:
                    Append(sb, Left, chunk.LeftStart, chunk.LeftEnd, newline);
                    break;
                case MergeChunkKind.RightOnly:
                    Append(sb, Right, chunk.RightStart, chunk.RightEnd, newline);
                    break;
                case MergeChunkKind.Conflict:
                    sb.Append("<<<<<<< ").Append(labels.Left).Append(newline);
                    Append(sb, Left, chunk.LeftStart, chunk.LeftEnd, newline, forceTerminator: true);
                    if (includeBase)
                    {
                        sb.Append("||||||| ").Append(labels.Base).Append(newline);
                        Append(sb, Base, chunk.BaseStart, chunk.BaseEnd, newline, forceTerminator: true);
                    }

                    sb.Append("=======").Append(newline);
                    Append(sb, Right, chunk.RightStart, chunk.RightEnd, newline, forceTerminator: true);
                    sb.Append(">>>>>>> ").Append(labels.Right).Append(newline);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, TextDocument doc, int start, int end, string newline, bool forceTerminator = false)
    {
        for (var i = start; i < end; i++)
        {
            var line = doc[i];
            sb.Append(line.Content);
            sb.Append(line.Ending == LineEnding.None && forceTerminator ? newline : line.EndingText);
        }
    }

    private string DetectNewline()
    {
        foreach (var doc in new[] { Base, Left, Right })
        {
            foreach (var line in doc.Lines)
            {
                if (line.Ending != LineEnding.None)
                {
                    return line.EndingText;
                }
            }
        }

        return Environment.NewLine;
    }
}

/// <summary>
/// Classic diff3 merge: diffs base against each side, groups overlapping changes and classifies each
/// group as taken from one side, identical on both sides, or a conflict.
/// </summary>
public static class ThreeWayMerger
{
    public static MergeResult Merge(
        TextDocument @base,
        TextDocument left,
        TextDocument right,
        CompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CompareOptions.Default;

        var normalizer = new LineNormalizer(options);
        var baseKeys = normalizer.ToKeys(@base);
        var leftKeys = normalizer.ToKeys(left);
        var rightKeys = normalizer.ToKeys(right);

        var leftHunks = TextComparer.ComputeHunks(baseKeys, leftKeys, options.Algorithm, cancellationToken);
        var rightHunks = TextComparer.ComputeHunks(baseKeys, rightKeys, options.Algorithm, cancellationToken);

        var chunks = new List<MergeChunk>();
        int li = 0, ri = 0;
        int basePos = 0, leftDelta = 0, rightDelta = 0;

        while (li < leftHunks.Count || ri < rightHunks.Count)
        {
            // Start a group with whichever change begins first in the base.
            var startsWithLeft = ri >= rightHunks.Count
                || (li < leftHunks.Count && leftHunks[li].LeftStart <= rightHunks[ri].LeftStart);
            var groupStart = startsWithLeft ? leftHunks[li].LeftStart : rightHunks[ri].LeftStart;
            var groupEnd = groupStart;
            int firstLeft = li, firstRight = ri;

            // Absorb every change, from either side, that overlaps or touches the group.
            var grew = true;
            while (grew)
            {
                grew = false;
                while (li < leftHunks.Count && leftHunks[li].LeftStart <= groupEnd)
                {
                    groupEnd = Math.Max(groupEnd, leftHunks[li].LeftEnd);
                    li++;
                    grew = true;
                }

                while (ri < rightHunks.Count && rightHunks[ri].LeftStart <= groupEnd)
                {
                    groupEnd = Math.Max(groupEnd, rightHunks[ri].LeftEnd);
                    ri++;
                    grew = true;
                }
            }

            if (basePos < groupStart)
            {
                chunks.Add(new MergeChunk(
                    MergeChunkKind.Unchanged,
                    basePos, groupStart,
                    basePos + leftDelta, groupStart + leftDelta,
                    basePos + rightDelta, groupStart + rightDelta));
            }

            var leftStart = groupStart + leftDelta;
            var rightStart = groupStart + rightDelta;
            leftDelta += SumDelta(leftHunks, firstLeft, li);
            rightDelta += SumDelta(rightHunks, firstRight, ri);
            var leftEnd = groupEnd + leftDelta;
            var rightEnd = groupEnd + rightDelta;

            var leftChanged = li > firstLeft;
            var rightChanged = ri > firstRight;
            MergeChunkKind kind;
            if (leftChanged && !rightChanged)
            {
                kind = MergeChunkKind.LeftOnly;
            }
            else if (rightChanged && !leftChanged)
            {
                kind = MergeChunkKind.RightOnly;
            }
            else
            {
                kind = SameLines(leftKeys, leftStart, leftEnd, rightKeys, rightStart, rightEnd)
                    ? MergeChunkKind.BothSame
                    : MergeChunkKind.Conflict;
            }

            chunks.Add(new MergeChunk(kind, groupStart, groupEnd, leftStart, leftEnd, rightStart, rightEnd));
            basePos = groupEnd;
        }

        if (basePos < @base.LineCount)
        {
            chunks.Add(new MergeChunk(
                MergeChunkKind.Unchanged,
                basePos, @base.LineCount,
                basePos + leftDelta, @base.LineCount + leftDelta,
                basePos + rightDelta, @base.LineCount + rightDelta));
        }

        return new MergeResult(@base, left, right, chunks);
    }

    private static int SumDelta(List<DiffHunk> hunks, int from, int to)
    {
        var delta = 0;
        for (var i = from; i < to; i++)
        {
            delta += hunks[i].RightCount - hunks[i].LeftCount;
        }

        return delta;
    }

    private static bool SameLines(int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd)
    {
        if (aEnd - aStart != bEnd - bStart)
        {
            return false;
        }

        for (var k = 0; k < aEnd - aStart; k++)
        {
            if (a[aStart + k] != b[bStart + k])
            {
                return false;
            }
        }

        return true;
    }
}
