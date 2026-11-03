using DiffRapid.Core.Algorithms;

namespace DiffRapid.Core.Comparison;

/// <summary>How whitespace differences are treated when comparing lines.</summary>
public enum WhitespaceMode
{
    /// <summary>Whitespace is compared exactly.</summary>
    Compare,

    /// <summary>Runs of whitespace compare equal regardless of length; leading/trailing whitespace is ignored.</summary>
    IgnoreChange,

    /// <summary>All whitespace is removed before comparing.</summary>
    IgnoreAll,
}

/// <summary>Granularity used to highlight differences inside a changed line.</summary>
public enum InlineGranularity
{
    Word,
    Character,
}

/// <summary>Options that control a text comparison. Immutable; use <c>with</c> to derive variants.</summary>
public sealed record CompareOptions
{
    public static readonly CompareOptions Default = new();

    public DiffAlgorithmKind Algorithm { get; init; } = DiffAlgorithmKind.Histogram;

    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.Compare;

    public bool IgnoreCase { get; init; }

    /// <summary>Changes consisting only of blank lines are classified as ignored.</summary>
    public bool IgnoreBlankLines { get; init; }

    /// <summary>CRLF, LF and CR line endings compare equal.</summary>
    public bool IgnoreLineEndings { get; init; }

    /// <summary>Sequences of digits compare equal regardless of value.</summary>
    public bool IgnoreNumbers { get; init; }

    /// <summary>A last line without a terminator compares equal to one with a terminator.</summary>
    public bool IgnoreMissingTrailingLineEnding { get; init; }

    public bool DetectMovedBlocks { get; init; } = true;

    public InlineGranularity InlineGranularity { get; init; } = InlineGranularity.Word;
}
