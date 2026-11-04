using System;
using System.Collections.Generic;
using System.Threading;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>
/// Finds runs of lines that were removed from one hunk and inserted, identical, into a different hunk.
/// </summary>
internal static class MovedBlockDetector
{
    /// <summary>Lines shorter than this (ignoring whitespace) only count as part of a longer move.</summary>
    private const int MinSignificantChars = 4;

    private const int MinLinesForTrivialMove = 3;

    public static IReadOnlyList<MovedBlock> Detect(
        IReadOnlyList<DiffHunk> hunks, TextDocument left, int[] leftKeys, int[] rightKeys, CancellationToken cancellationToken)
    {
        // A move needs two distinct hunks; avoid indexing an entire replacement from the cost fallback.
        if (hunks.Count < 2)
        {
            return Array.Empty<MovedBlock>();
        }

        var removedHunk = new int[leftKeys.Length];
        var addedHunk = new int[rightKeys.Length];
        for (var i = 0; i < removedHunk.Length; i++)
        {
            removedHunk[i] = -1;
        }

        for (var j = 0; j < addedHunk.Length; j++)
        {
            addedHunk[j] = -1;
        }

        var addedByKey = new Dictionary<int, List<int>>();
        for (var h = 0; h < hunks.Count; h++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hunk = hunks[h];
            if (hunk.Significance != DiffSignificance.Important)
            {
                continue;
            }

            for (var i = hunk.LeftStart; i < hunk.LeftEnd; i++)
            {
                removedHunk[i] = h;
            }

            for (var j = hunk.RightStart; j < hunk.RightEnd; j++)
            {
                addedHunk[j] = h;
                if (!addedByKey.TryGetValue(rightKeys[j], out var list))
                {
                    list = new List<int>();
                    addedByKey.Add(rightKeys[j], list);
                }

                list.Add(j);
            }
        }

        var usedRight = new bool[rightKeys.Length];
        var moves = new List<MovedBlock>();
        var li = 0;
        while (li < leftKeys.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (removedHunk[li] < 0 || !addedByKey.TryGetValue(leftKeys[li], out var candidates))
            {
                li++;
                continue;
            }

            int bestStart = -1, bestLength = 0;
            foreach (var rj in candidates)
            {
                if (usedRight[rj] || addedHunk[rj] == removedHunk[li])
                {
                    continue;
                }

                var length = 0;
                while (li + length < leftKeys.Length
                    && rj + length < rightKeys.Length
                    && removedHunk[li + length] >= 0
                    && addedHunk[rj + length] >= 0
                    && addedHunk[rj + length] != removedHunk[li + length]
                    && !usedRight[rj + length]
                    && leftKeys[li + length] == rightKeys[rj + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = rj;
                }
            }

            if (bestLength > 0 && IsSignificant(left, li, bestLength))
            {
                for (var k = 0; k < bestLength; k++)
                {
                    usedRight[bestStart + k] = true;
                }

                moves.Add(new MovedBlock(li, bestStart, bestLength));
                li += bestLength;
            }
            else
            {
                li++;
            }
        }

        return moves;
    }

    // Avoids reporting moves of lone braces or blank lines, which are almost always coincidental.
    private static bool IsSignificant(TextDocument left, int start, int count)
    {
        if (count >= MinLinesForTrivialMove)
        {
            return true;
        }

        for (var i = start; i < start + count; i++)
        {
            var nonWhitespace = 0;
            foreach (var c in left[i].Content)
            {
                if (!char.IsWhiteSpace(c))
                {
                    nonWhitespace++;
                }
            }

            if (nonWhitespace >= MinSignificantChars)
            {
                return true;
            }
        }

        return false;
    }
}
