using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>
/// Linear-space Myers diff ("An O(ND) Difference Algorithm and Its Variations", E. Myers, 1986),
/// using the middle-snake divide and conquer.
/// </summary>
public sealed class MyersDiff : IDiffAlgorithm
{
    public void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        CompareRange(a, 0, a.Length, b, 0, b.Length, changedA, changedB, cancellationToken);
    }

    /// <summary>Diffs a sub-range; used by the anchoring algorithms as their fallback.</summary>
    internal static void CompareRange(
        int[] a, int aLo, int aHi, int[] b, int bLo, int bHi,
        bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        var context = new Context(a, b, changedA, changedB, (aHi - aLo) + (bHi - bLo), cancellationToken);
        context.Diff(aLo, aHi, bLo, bHi);
    }

    private sealed class Context
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _changedA;
        private readonly bool[] _changedB;
        private readonly CancellationToken _cancellationToken;
        private readonly int[] _forward;
        private readonly int[] _backward;
        private readonly int _offset;

        public Context(int[] a, int[] b, bool[] changedA, bool[] changedB, int size, CancellationToken cancellationToken)
        {
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

                FindMiddleSnake(aLo, aHi, bLo, bHi, out var startX, out var startY, out var endX, out var endY);

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
        private void FindMiddleSnake(int aLo, int aHi, int bLo, int bHi, out int startX, out int startY, out int endX, out int endY)
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
                        return;
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
                        return;
                    }
                }
            }

            // Unreachable for valid input: the two searches always meet within maxD steps.
            startX = endX = aLo;
            startY = endY = bLo;
        }
    }
}
