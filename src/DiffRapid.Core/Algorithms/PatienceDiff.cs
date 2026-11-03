using System.Collections.Generic;
using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>
/// Patience diff: anchors on elements that occur exactly once on each side, keeps the longest
/// increasing run of those anchors, recurses between them and falls back to Myers when no unique
/// anchors exist.
/// </summary>
public sealed class PatienceDiff : IDiffAlgorithm
{
    public void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        Diff(a, 0, a.Length, b, 0, b.Length, changedA, changedB, cancellationToken);
    }

    private static void Diff(
        int[] a, int aLo, int aHi, int[] b, int bLo, int bHi,
        bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
        {
            aLo++;
            bLo++;
        }

        while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
        {
            aHi--;
            bHi--;
        }

        if (aLo == aHi || bLo == bHi)
        {
            MyersDiff.CompareRange(a, aLo, aHi, b, bLo, bHi, changedA, changedB, cancellationToken);
            return;
        }

        var anchors = FindUniqueAnchors(a, aLo, aHi, b, bLo, bHi);
        if (anchors.Count == 0)
        {
            MyersDiff.CompareRange(a, aLo, aHi, b, bLo, bHi, changedA, changedB, cancellationToken);
            return;
        }

        var prevA = aLo;
        var prevB = bLo;
        foreach (var (ai, bj) in anchors)
        {
            Diff(a, prevA, ai, b, prevB, bj, changedA, changedB, cancellationToken);
            prevA = ai + 1;
            prevB = bj + 1;
        }

        Diff(a, prevA, aHi, b, prevB, bHi, changedA, changedB, cancellationToken);
    }

    // Returns the longest chain of (aIndex, bIndex) pairs of unique-on-both-sides elements that is
    // increasing on both sides.
    private static List<(int A, int B)> FindUniqueAnchors(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
    {
        var counts = new Dictionary<int, Occurrence>();
        for (var i = aLo; i < aHi; i++)
        {
            counts.TryGetValue(a[i], out var occ);
            occ.CountA++;
            occ.PosA = i;
            counts[a[i]] = occ;
        }

        for (var j = bLo; j < bHi; j++)
        {
            if (counts.TryGetValue(b[j], out var occ))
            {
                occ.CountB++;
                occ.PosB = j;
                counts[b[j]] = occ;
            }
        }

        var unique = new List<(int A, int B)>();
        for (var i = aLo; i < aHi; i++)
        {
            var occ = counts[a[i]];
            if (occ.CountA == 1 && occ.CountB == 1)
            {
                unique.Add((i, occ.PosB));
            }
        }

        return LongestIncreasingByB(unique);
    }

    private static List<(int A, int B)> LongestIncreasingByB(List<(int A, int B)> pairs)
    {
        var result = new List<(int A, int B)>();
        if (pairs.Count == 0)
        {
            return result;
        }

        // Patience sorting: tails[k] = index of the smallest tail of an increasing run of length k+1.
        var tails = new List<int>();
        var previous = new int[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (pairs[tails[mid]].B < pairs[i].B)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        for (var k = tails[tails.Count - 1]; k >= 0; k = previous[k])
        {
            result.Add(pairs[k]);
        }

        result.Reverse();
        return result;
    }

    private struct Occurrence
    {
        public int CountA;
        public int CountB;
        public int PosA;
        public int PosB;
    }
}
