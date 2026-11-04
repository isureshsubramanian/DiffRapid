using System;
using System.Collections.Generic;

namespace DiffRapid.Core.Table;

/// <summary>A table read from a delimited text file: an optional header and ragged data rows.</summary>
public sealed class TableData
{
    public TableData(
        IReadOnlyList<string>? header,
        IReadOnlyList<IReadOnlyList<string>> rows,
        char delimiter = ',',
        IReadOnlyList<int>? lineNumbers = null)
    {
        Header = header;
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Delimiter = delimiter;
        if (lineNumbers is not null && lineNumbers.Count != rows.Count)
        {
            throw new ArgumentException("There must be one line number per row.", nameof(lineNumbers));
        }

        LineNumbers = lineNumbers;
        var count = header?.Count ?? 0;
        foreach (var row in rows)
        {
            count = Math.Max(count, row.Count);
        }

        ColumnCount = count;
    }

    /// <summary>Column names from the first record, or null when the file has no header row.</summary>
    public IReadOnlyList<string>? Header { get; }

    /// <summary>Data rows; rows may have different numbers of cells.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>The delimiter the table was read with.</summary>
    public char Delimiter { get; }

    /// <summary>One-based file line where each data row starts, or null when the table was not read from text.</summary>
    public IReadOnlyList<int>? LineNumbers { get; }

    /// <summary>The widest row (or the header, if wider).</summary>
    public int ColumnCount { get; }

    /// <summary>The cell at a zero-based row and column; a missing cell of a short row reads as empty.</summary>
    public string Cell(int row, int column)
    {
        var cells = Rows[row];
        return column < cells.Count ? cells[column] : string.Empty;
    }

    /// <summary>The one-based line a row starts on, or the one-based row number when line numbers are unknown.</summary>
    public int LineOf(int row) => LineNumbers is null ? row + 1 : LineNumbers[row];
}
