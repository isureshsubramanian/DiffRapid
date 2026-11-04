using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>Strips the common prefix and suffix and treats the remainder as a single change.</summary>
public sealed class NoneDiff : IDiffAlgorithm
{
    public void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken)
    {
        int aLo = 0, bLo = 0, aHi = a.Length, bHi = b.Length;
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

        for (var i = aLo; i < aHi; i++)
        {
            changedA[i] = true;
        }

        for (var j = bLo; j < bHi; j++)
        {
            changedB[j] = true;
        }
    }
}
