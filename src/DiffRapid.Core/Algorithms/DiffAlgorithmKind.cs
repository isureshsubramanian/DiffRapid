namespace DiffRapid.Core.Algorithms;

/// <summary>Line-matching algorithms offered to the user (WinMerge-compatible set).</summary>
public enum DiffAlgorithmKind
{
    /// <summary>Myers O(ND), the classic default.</summary>
    Default,

    /// <summary>Myers without speed heuristics; always produces a minimal edit script.</summary>
    Minimal,

    /// <summary>Anchors on lines that are unique on both sides, then recurses.</summary>
    Patience,

    /// <summary>Anchors on the least-frequent common lines; usually the most readable result.</summary>
    Histogram,

    /// <summary>Only strips the common prefix and suffix; everything in between is one change.</summary>
    None,
}
