using System.Collections.Generic;
using System.Linq;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>The outcome of comparing two text documents.</summary>
public sealed class TextDiffResult
{
    public TextDiffResult(
        TextDocument left,
        TextDocument right,
        CompareOptions options,
        IReadOnlyList<DiffHunk> hunks,
        IReadOnlyList<MovedBlock> movedBlocks)
    {
        Left = left;
        Right = right;
        Options = options;
        Hunks = hunks;
        MovedBlocks = movedBlocks;
    }

    public TextDocument Left { get; }

    public TextDocument Right { get; }

    public CompareOptions Options { get; }

    /// <summary>All differing blocks in document order, including ignored ones.</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; }

    public IReadOnlyList<MovedBlock> MovedBlocks { get; }

    public IEnumerable<DiffHunk> ImportantHunks => Hunks.Where(h => h.Significance == DiffSignificance.Important);

    public int DifferenceCount => ImportantHunks.Count();

    /// <summary>True when there is no important difference.</summary>
    public bool AreEqual => !ImportantHunks.Any();
}
