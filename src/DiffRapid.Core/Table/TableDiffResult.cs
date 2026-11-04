using System.Collections.Generic;
using System.Linq;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Table;

/// <summary>How a data row compares between the two tables.</summary>
public enum TableRowStatus
{
    /// <summary>Every compared cell is equal under the options.</summary>
    Same,

    /// <summary>The row is on both sides and at least one compared cell differs.</summary>
    Changed,

    /// <summary>The row exists only in the left table.</summary>
    LeftOnly,

    /// <summary>The row exists only in the right table.</summary>
    RightOnly,

    /// <summary>The row is on both sides but in a different place relative to its neighbours.</summary>
    Moved,
}

/// <summary>Which side of a comparison something belongs to.</summary>
public enum TableSide
{
    Left,
    Right,
}

/// <summary>A column of the comparison and where it sits in each table.</summary>
/// <param name="Name">The header name (left name first), or "Column n" when there is no header.</param>
/// <param name="LeftIndex">Zero-based column in the left table, or null when the column is only on the right.</param>
/// <param name="RightIndex">Zero-based column in the right table, or null when the column is only on the left.</param>
/// <param name="LeftName">The left header text, if any.</param>
/// <param name="RightName">The right header text, if any.</param>
/// <param name="IsKey">The column is one of the key columns used to match rows.</param>
/// <param name="IsIgnored">The column is excluded from comparison.</param>
public sealed record TableColumn(
    string Name,
    int? LeftIndex,
    int? RightIndex,
    string? LeftName,
    string? RightName,
    bool IsKey,
    bool IsIgnored)
{
    /// <summary>Both sides have the column, so its cells are compared (unless ignored).</summary>
    public bool IsCompared => LeftIndex is not null && RightIndex is not null && !IsIgnored;

    /// <summary>The header names differ although the columns were paired by position.</summary>
    public bool IsRenamed => LeftName is not null && RightName is not null && LeftName != RightName;
}

/// <summary>One compared cell of a row that is on both sides.</summary>
/// <param name="Column">Index into <see cref="TableDiffResult.Columns"/>.</param>
/// <param name="Left">The left cell text.</param>
/// <param name="Right">The right cell text.</param>
/// <param name="IsChanged">The cells differ under the comparison options.</param>
/// <param name="Inline">Changed word ranges within each cell, or null when the cells are equal.</param>
public sealed record TableCellDiff(int Column, string Left, string Right, bool IsChanged, InlineDiff? Inline);

/// <summary>A row of the comparison.</summary>
/// <param name="Status">How the row compares.</param>
/// <param name="LeftRow">Zero-based data row in the left table, or null for a right-only row.</param>
/// <param name="RightRow">Zero-based data row in the right table, or null for a left-only row.</param>
/// <param name="Cells">
/// Every compared cell for Changed and Moved rows; empty for Same, LeftOnly and RightOnly rows, whose text
/// is read from the tables.
/// </param>
public sealed record TableRowDiff(TableRowStatus Status, int? LeftRow, int? RightRow, IReadOnlyList<TableCellDiff> Cells)
{
    /// <summary>At least one compared cell differs (a Moved row may also have changed cells).</summary>
    public bool HasChangedCells => Cells.Any(c => c.IsChanged);
}

/// <summary>A key value found on more than one row of one table.</summary>
/// <param name="Side">The table with the duplicates.</param>
/// <param name="Key">The key cell values, in key column order.</param>
/// <param name="Rows">The zero-based data rows that share the key.</param>
public sealed record TableDuplicateKey(TableSide Side, IReadOnlyList<string> Key, IReadOnlyList<int> Rows);

/// <summary>The outcome of comparing two tables.</summary>
public sealed class TableDiffResult
{
    public TableDiffResult(
        TableData left,
        TableData right,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<TableRowDiff> rows,
        IReadOnlyList<TableDuplicateKey> duplicateKeys)
    {
        Left = left;
        Right = right;
        Columns = columns;
        Rows = rows;
        DuplicateKeys = duplicateKeys;
    }

    public TableData Left { get; }

    public TableData Right { get; }

    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>Rows in display order: matched rows follow the left table, with right-only rows where they occur.</summary>
    public IReadOnlyList<TableRowDiff> Rows { get; }

    /// <summary>Key values that occur more than once on one side; the n-th duplicate is matched with the n-th on the other side.</summary>
    public IReadOnlyList<TableDuplicateKey> DuplicateKeys { get; }

    /// <summary>Number of rows with the given status.</summary>
    public int Count(TableRowStatus status) => Rows.Count(r => r.Status == status);

    /// <summary>Columns that are on one side only or were renamed, ignoring ignored columns.</summary>
    public IEnumerable<TableColumn> ColumnDifferences =>
        Columns.Where(c => !c.IsIgnored && (c.LeftIndex is null || c.RightIndex is null || c.IsRenamed));

    /// <summary>No row differs and no compared column is missing or renamed.</summary>
    public bool AreIdentical => Rows.All(r => r.Status == TableRowStatus.Same) && !ColumnDifferences.Any();
}
