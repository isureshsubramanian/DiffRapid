using System;
using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>
/// Histogram diff: an extension of patience diff that anchors on the longest common region built
/// around the lowest-occurrence element, so it also works when no element is unique. Regions whose
/// rarest element occurs more than <see cref="MaxChainLength"/> times fall back to Myers.
/// </summary>
public sealed class HistogramDiff : IDiffAlgorithm
{
    public const int MaxChainLength = 64;

    private const int MaxDepth = 64;

    public void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        var maxKey = -1;
        foreach (var key in a)
        {
            if (key < 0)
            {
                throw new ArgumentException("Keys must be non-negative.", nameof(a));
            }

            maxKey = Math.Max(maxKey, key);
        }

        var context = new Context(a, b, changedA, changedB, maxKey, cancellationToken);
        context.Diff(0, a.Length, 0, b.Length, 0);
    }

    private sealed class Context
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _changedA;
        private readonly bool[] _changedB;
        private readonly CancellationToken _cancellationToken;

        // Occurrence index for the current region of A, reused across regions:
        // _count[key] = occurrences, _first[key] = first position, _next[i] = next position with the same key.
        private readonly int[] _count;
        private readonly int[] _first;
        private readonly int[] _next;

        public Context(int[] a, int[] b, bool[] changedA, bool[] changedB, int maxKey, CancellationToken cancellationToken)
        {
            _a = a;
            _b = b;
            _changedA = changedA;
            _changedB = changedB;
            _cancellationToken = cancellationToken;
            _count = new int[maxKey + 1];
            _first = new int[maxKey + 1];
            _next = new int[a.Length];
        }

        public void Diff(int aLo, int aHi, int bLo, int bHi, int depth)
        {
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo])
                {
                    aLo++;
                    bLo++;
                }

                while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1])
                {
                    aHi--;
                    bHi--;
                }

                if (aLo == aHi || bLo == bHi || depth > MaxDepth
                    || !FindBestRegion(aLo, aHi, bLo, bHi, out var aStart, out var aEnd, out var bStart, out var bEnd))
                {
                    MyersDiff.CompareRange(_a, aLo, aHi, _b, bLo, bHi, _changedA, _changedB, _cancellationToken);
                    return;
                }

                Diff(aLo, aStart, bLo, bStart, depth + 1);
                aLo = aEnd;
                bLo = bEnd;
                depth++;
            }
        }

        private bool FindBestRegion(int aLo, int aHi, int bLo, int bHi, out int bestAStart, out int bestAEnd, out int bestBStart, out int bestBEnd)
        {
            for (var i = aHi - 1; i >= aLo; i--)
            {
                var key = _a[i];
                _next[i] = _count[key] == 0 ? -1 : _first[key];
                _first[key] = i;
                _count[key]++;
            }

            bestAStart = bestAEnd = bestBStart = bestBEnd = 0;
            var bestCount = MaxChainLength + 1;
            var bestLength = 0;

            var j = bLo;
            while (j < bHi)
            {
                var next = j + 1;
                var key = _b[j];
                var occurrences = key < _count.Length ? _count[key] : 0;
                if (occurrences > 0 && occurrences <= bestCount)
                {
                    for (var ai = _first[key]; ai >= 0; ai = _next[ai])
                    {
                        int sa = ai, sb = j, ea = ai + 1, eb = j + 1;
                        var count = occurrences;

                        while (sa > aLo && sb > bLo && _a[sa - 1] == _b[sb - 1])
                        {
                            sa--;
                            sb--;
                            count = Math.Min(count, _count[_a[sa]]);
                        }

                        while (ea < aHi && eb < bHi && _a[ea] == _b[eb])
                        {
                            count = Math.Min(count, _count[_a[ea]]);
                            ea++;
                            eb++;
                        }

                        var length = ea - sa;
                        if (count < bestCount || (count == bestCount && length > bestLength))
                        {
                            bestCount = count;
                            bestLength = length;
                            bestAStart = sa;
                            bestAEnd = ea;
                            bestBStart = sb;
                            bestBEnd = eb;
                        }

                        if (eb > next)
                        {
                            next = eb;
                        }
                    }
                }

                j = next;
            }

            // Reset only the entries this region touched.
            for (var i = aLo; i < aHi; i++)
            {
                _count[_a[i]] = 0;
            }

            return bestCount <= MaxChainLength;
        }
    }
}
