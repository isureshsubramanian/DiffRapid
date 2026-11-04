using System;
using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>
/// Linear-space Myers diff ("An O(ND) Difference Algorithm and Its Variations", E. Myers, 1986),
/// using the middle-snake divide and conquer.
/// A search is limited to a number of frontier visits that grows with the input (see <see cref="SearchBudget"/>);
/// ranges left when it runs out become replacements. Memory remains linear and the edit script remains valid,
/// but may not be minimal. The minimal variant has no limit.
/// </summary>
public sealed class MyersDiff : IDiffAlgorithm
{
    private readonly bool _minimal;

    /// <summary>Creates a linear-space search with a bounded replacement fallback.</summary>
    public MyersDiff()
    {
    }

    internal MyersDiff(bool minimal)
    {
        _minimal = minimal;
    }

    public void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var size = a.Length + b.Length;
        var budget = _minimal ? long.MaxValue : SearchBudget(size);
        var context = new Context(a, b, changedA, changedB, size, budget, cancellationToken);
        context.Diff(0, a.Length, 0, b.Length);
    }

    /// <summary>Diffs a sub-range; used by the anchoring algorithms as their fallback.</summary>
    internal static void CompareRange(
        int[] a, int aLo, int aHi, int[] b, int bLo, int bHi,
        bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        var size = (aHi - aLo) + (bHi - bLo);
        var context = new Context(a, b, changedA, changedB, size, SearchBudget(size), cancellationToken);
        context.Diff(aLo, aHi, bLo, bHi);
    }

    /// <summary>
    /// Frontier visits allowed for an input of <paramref name="size"/> lines: at least a million, and 256 per line,
    /// so a 400k-line pair may cost about 100 million cheap steps. The cost of a search grows with the square of the
    /// number of edits, so this keeps the exact result for some ten thousand changed lines in a large file, instead
    /// of turning a file with a few thousand changes into one replacement.
    /// </summary>
    internal static long SearchBudget(int size) => Math.Max(1000000L, 256L * size);

    private sealed class Context
    {
        private long _remainingSteps;
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _changedA;
        private readonly bool[] _changedB;
        private readonly CancellationToken _cancellationToken;
        private readonly int[] _forward;
        private readonly int[] _backward;
        private readonly int _offset;

        public Context(int[] a, int[] b, bool[] changedA, bool[] changedB, int size, long maxSearchSteps, CancellationToken cancellationToken)
        {
            _remainingSteps = maxSearchSteps;
            _a = a;
            _b = b;
            _changedA = changedA;
            _changedB = changedB;
            _cancellationToken = cancellationToken;
            _offset = size + 2;
            _forward = new int[(2 * _offset) + 1];
            _backward = new int[(2 * _offset) + 1];
        }

        public void Diff(int aLo, int aHi, int bLo, int bHi)
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

                if (aLo == aHi)
                {
                    for (var j = bLo; j < bHi; j++)
                    {
                        _changedB[j] = true;
                    }

                    return;
                }

                if (bLo == bHi)
                {
                    for (var i = aLo; i < aHi; i++)
                    {
                        _changedA[i] = true;
                    }

                    return;
                }

                if (!TryFindMiddleSnake(aLo, aHi, bLo, bHi, out var startX, out var startY, out var endX, out var endY))
                {
                    MarkChanged(_changedA, aLo, aHi);
                    MarkChanged(_changedB, bLo, bHi);
                    return;
                }

                // Recurse on the smaller half and loop on the other to bound stack depth.
                if ((startX - aLo) + (startY - bLo) < (aHi - endX) + (bHi - endY))
                {
                    Diff(aLo, startX, bLo, startY);
                    aLo = endX;
                    bLo = endY;
                }
                else
                {
                    Diff(endX, aHi, endY, bHi);
                    aHi = startX;
                    bHi = startY;
                }
            }
        }

        // Finds a snake (diagonal run of matches) on an optimal edit path, returned in absolute
        // coordinates as [start, end). Callers guarantee the first and last elements differ.
        private bool TryFindMiddleSnake(int aLo, int aHi, int bLo, int bHi, out int startX, out int startY, out int endX, out int endY)
        {
            var n = aHi - aLo;
            var m = bHi - bLo;
            var delta = n - m;
            var odd = (delta & 1) != 0;
            var maxD = ((n + m + 1) / 2) + 1;
            var o = _offset;
            var vf = _forward;
            var vb = _backward;
            vf[o + 1] = 0;
            vb[o + 1] = 0;

            for (var d = 0; d <= maxD; d++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var steps = 2 * (d + 1);
                if (_remainingSteps < steps)
                {
                    startX = endX = aLo;
                    startY = endY = bLo;
                    return false;
                }

                _remainingSteps -= steps;

                for (var k = -d; k <= d; k += 2)
                {
                    var x = (k == -d || (k != d && vf[o + k - 1] < vf[o + k + 1])) ? vf[o + k + 1] : vf[o + k - 1] + 1;
                    var y = x - k;
                    var sx = x;
                    var sy = y;
                    while (x < n && y < m && _a[aLo + x] == _b[bLo + y])
                    {
                        x++;
                        y++;
                    }

                    vf[o + k] = x;

                    var reverseK = delta - k;
                    if (odd && reverseK >= -(d - 1) && reverseK <= d - 1 && x + vb[o + reverseK] >= n)
                    {
                        startX = aLo + sx;
                        startY = bLo + sy;
                        endX = aLo + x;
                        endY = bLo + y;
                        return true;
                    }
                }

                for (var k = -d; k <= d; k += 2)
                {
                    var x = (k == -d || (k != d && vb[o + k - 1] < vb[o + k + 1])) ? vb[o + k + 1] : vb[o + k - 1] + 1;
                    var y = x - k;
                    var sx = x;
                    var sy = y;
                    while (x < n && y < m && _a[aHi - 1 - x] == _b[bHi - 1 - y])
                    {
                        x++;
                        y++;
                    }

                    vb[o + k] = x;

                    var forwardK = delta - k;
                    if (!odd && forwardK >= -d && forwardK <= d && x + vf[o + forwardK] >= n)
                    {
                        // Convert the reverse snake back to forward coordinates.
                        startX = aHi - x;
                        startY = bHi - y;
                        endX = aHi - sx;
                        endY = bHi - sy;
                        return true;
                    }
                }
            }

            // Unreachable for valid input: the two searches always meet within maxD steps.
            startX = endX = aLo;
            startY = endY = bLo;
            return false;
        }

        private static void MarkChanged(bool[] changed, int start, int end)
        {
            for (var index = start; index < end; index++)
            {
                changed[index] = true;
            }
        }
    }
}
