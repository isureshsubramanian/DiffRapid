using System.Threading;

namespace DiffRapid.Core.Algorithms;

/// <summary>
/// Compares two sequences of interned tokens and marks the elements that are not part of the
/// common subsequence it chose.
/// </summary>
public interface IDiffAlgorithm
{
    /// <param name="a">Left sequence.</param>
    /// <param name="b">Right sequence.</param>
    /// <param name="changedA">Receives true for every element of <paramref name="a"/> that was removed.</param>
    /// <param name="changedB">Receives true for every element of <paramref name="b"/> that was inserted.</param>
    /// <param name="cancellationToken">Cancels long comparisons.</param>
    void Compare(int[] a, int[] b, bool[] changedA, bool[] changedB, CancellationToken cancellationToken);
}
