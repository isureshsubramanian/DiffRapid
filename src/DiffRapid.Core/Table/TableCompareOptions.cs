using System;
using System.Collections.Generic;
using DiffRapid.Core.Algorithms;

namespace DiffRapid.Core.Table;

/// <summary>
/// Options that control a table comparison. Columns are named by header text or by one-based column number
/// ("3"); a header name wins when both could apply.
/// </summary>
public sealed record TableCompareOptions
{
    public static readonly TableCompareOptions Default = new();

    /// <summary>Match rows by these columns instead of by position. Empty compares rows in order with a line diff.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();

    /// <summary>Columns left out of the comparison.</summary>
    public IReadOnlyList<string> IgnoreColumns { get; init; } = Array.Empty<string>();

    /// <summary>Cells that differ only in letter case compare equal.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Leading and trailing whitespace in cells is ignored.</summary>
    public bool TrimWhitespace { get; init; }

    /// <summary>Numbers whose difference is at most this much compare equal. Zero compares numbers as text.</summary>
    public double AbsoluteTolerance { get; init; }

    /// <summary>
    /// Numbers whose difference is at most this fraction of the larger magnitude compare equal
    /// (0.01 = 1%). Zero compares numbers as text.
    /// </summary>
    public double RelativeTolerance { get; init; }

    /// <summary>When both tables have a header, pair columns by name so a different column order is not a difference.</summary>
    public bool MatchColumnsByName { get; init; } = true;

    /// <summary>Report rows that appear elsewhere on the other side as moved rather than removed and added.</summary>
    public bool DetectMovedRows { get; init; } = true;

    /// <summary>The line diff used to align rows when no key columns are given.</summary>
    public DiffAlgorithmKind Algorithm { get; init; } = DiffAlgorithmKind.Histogram;
}
