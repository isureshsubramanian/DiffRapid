using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>Compares two text documents line by line.</summary>
public static class TextComparer
{
    public static TextDiffResult Compare(
        TextDocument left,
        TextDocument right,
        CompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options ??= CompareOptions.Default;

        var normalizer = new LineNormalizer(options);
        var leftKeys = normalizer.ToKeys(left);
        var rightKeys = normalizer.ToKeys(right);

        var hunks = ComputeHunks(leftKeys, rightKeys, options.Algorithm, cancellationToken);

        if (options.IgnoreBlankLines)
        {
            hunks = ClassifyBlankLineHunks(hunks, left, right);
        }

        IReadOnlyList<MovedBlock> moved = options.DetectMovedBlocks
            ? MovedBlockDetector.Detect(hunks, left, leftKeys, rightKeys)
            : Array.Empty<MovedBlock>();

        return new TextDiffResult(left, right, options, hunks, moved);
    }

    /// <summary>Runs an algorithm over key sequences and groups the changed elements into hunks.</summary>
    public static List<DiffHunk> ComputeHunks(int[] a, int[] b, DiffAlgorithmKind algorithm, CancellationToken cancellationToken)
    {
        var changedA = new bool[a.Length];
        var changedB = new bool[b.Length];
        DiffAlgorithms.Create(algorithm).Compare(a, b, changedA, changedB, cancellationToken);
        return BuildHunks(changedA, changedB);
    }

    internal static List<DiffHunk> BuildHunks(bool[] changedA, bool[] changedB)
    {
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        while (i < changedA.Length || j < changedB.Length)
        {
            if (i < changedA.Length && j < changedB.Length && !changedA[i] && !changedB[j])
            {
                i++;
                j++;
                continue;
            }

            int startA = i, startB = j;
            while (i < changedA.Length && changedA[i])
            {
                i++;
            }

            while (j < changedB.Length && changedB[j])
            {
                j++;
            }

            if (i == startA && j == startB)
            {
                // Unmatched tail on one side only; cannot happen with a consistent algorithm.
                throw new InvalidOperationException("Diff algorithm produced an inconsistent edit script.");
            }

            hunks.Add(new DiffHunk(startA, i - startA, startB, j - startB));
        }

        return hunks;
    }

    private static List<DiffHunk> ClassifyBlankLineHunks(List<DiffHunk> hunks, TextDocument left, TextDocument right)
    {
        var result = new List<DiffHunk>(hunks.Count);
        foreach (var hunk in hunks)
        {
            var allBlank = true;
            for (var i = hunk.LeftStart; i < hunk.LeftEnd && allBlank; i++)
            {
                allBlank = LineNormalizer.IsBlank(left[i].Content);
            }

            for (var j = hunk.RightStart; j < hunk.RightEnd && allBlank; j++)
            {
                allBlank = LineNormalizer.IsBlank(right[j].Content);
            }

            result.Add(allBlank ? hunk with { Significance = DiffSignificance.Ignored } : hunk);
        }

        return result;
    }
}
