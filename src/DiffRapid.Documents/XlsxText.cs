using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DiffRapid.Documents.Internal;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DiffRapid.Documents;

/// <summary>What a cell with a formula shows in the extracted text.</summary>
public enum XlsxCellContent
{
    /// <summary>The cached value, as Excel displays it.</summary>
    Values,

    /// <summary>The formula ("=A1+B1"); cells without a formula show their value.</summary>
    Formulas,

    /// <summary>The formula and the value ("=A1+B1 -> 42").</summary>
    Both,
}

/// <summary>Options for <see cref="XlsxText"/>.</summary>
public sealed record XlsxTextOptions
{
    /// <summary>Values only.</summary>
    public static XlsxTextOptions Default { get; } = new();

    /// <summary>What a cell with a formula shows.</summary>
    public XlsxCellContent CellContent { get; init; } = XlsxCellContent.Values;
}

/// <summary>One worksheet as rows of cell text, ready for a table compare.</summary>
/// <param name="Name">The sheet's tab name.</param>
/// <param name="IsHidden">Whether the sheet is hidden in Excel.</param>
/// <param name="Rows">
/// Rows from row 1 to the last row with content. Empty rows in between are kept (as empty lists) so that
/// row numbers line up; trailing empty cells are left out of each row.
/// </param>
public sealed record XlsxSheet(string Name, bool IsHidden, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Turns an Excel workbook (.xlsx) into comparable text: every worksheet in tab order under a
/// "[Sheet: name]" label, one tab-separated line per row. Numbers use the invariant culture, dates and
/// times follow the cell's number format (as ISO 8601), and booleans show as TRUE or FALSE.
/// </summary>
public static class XlsxText
{
    /// <summary>Extracts the text of the workbook at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable Excel workbook.</exception>
    public static string Extract(string path, XlsxTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Extract(stream, options, cancellationToken);
    }

    /// <summary>Extracts the text of a workbook read from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable Excel workbook.</exception>
    public static string Extract(Stream stream, XlsxTextOptions? options = null, CancellationToken cancellationToken = default) =>
        Format(ReadSheets(stream, options, cancellationToken));

    /// <summary>Reads every worksheet of the workbook at <paramref name="path"/> as rows of cell text.</summary>
    /// <exception cref="InvalidDataException">The file is not a readable Excel workbook.</exception>
    public static IReadOnlyList<XlsxSheet> ReadSheets(string path, XlsxTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return ReadSheets(stream, options, cancellationToken);
    }

    /// <summary>Reads every worksheet of a workbook read from <paramref name="stream"/> as rows of cell text.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable Excel workbook.</exception>
    public static IReadOnlyList<XlsxSheet> ReadSheets(Stream stream, XlsxTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= XlsxTextOptions.Default;
        using var document = OpenXmlPackages.Open(stream, source => SpreadsheetDocument.Open(source, false), "Excel");
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("The Excel workbook has no workbook part.");
        var cells = new XlsxCellReader(workbookPart, options.CellContent);
        var sheets = new List<XlsxSheet>();
        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = sheet.Id?.Value;
            if (id is null || !workbookPart.TryGetPartById(id, out var part) || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var hidden = sheet.State?.Value is { } state && state != SheetStateValues.Visible;
            sheets.Add(new XlsxSheet(sheet.Name?.Value ?? string.Empty, hidden, ReadRows(worksheetPart, cells, cancellationToken)));
        }

        return sheets;
    }

    /// <summary>Writes sheets as text: a label line per sheet, then one tab-separated line per row.</summary>
    public static string Format(IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets is null)
        {
            throw new ArgumentNullException(nameof(sheets));
        }

        var lines = new TextLines();
        foreach (var sheet in sheets)
        {
            lines.Add($"[Sheet: {sheet.Name}{(sheet.IsHidden ? " (hidden)" : string.Empty)}]");
            foreach (var row in sheet.Rows)
            {
                lines.Add(string.Join("\t", row.Select(OneLine)));
            }
        }

        return lines.ToString();
    }

    private static string OneLine(string cell) =>
        cell.IndexOfAny(['\t', '\r', '\n']) < 0
            ? cell
            : cell.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    /// <summary>Streams the sheet's rows so that large sheets are not loaded as one DOM.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(WorksheetPart part, XlsxCellReader cells, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<string>>();
        var sharedFormulas = new Dictionary<uint, SharedFormula>();
        var lastContentRow = 0;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var row = (Row)reader.LoadCurrentElement()!;
            var rowNumber = (int?)row.RowIndex?.Value ?? rows.Count + 1;
            if (rowNumber <= rows.Count)
            {
                continue;
            }

            while (rows.Count < rowNumber - 1)
            {
                rows.Add(Array.Empty<string>());
            }

            var values = ReadCells(row, rowNumber, cells, sharedFormulas);
            rows.Add(values);
            if (values.Count > 0)
            {
                lastContentRow = rows.Count;
            }
        }

        rows.RemoveRange(lastContentRow, rows.Count - lastContentRow);
        return rows;
    }

    private static IReadOnlyList<string> ReadCells(Row row, int rowNumber, XlsxCellReader cells, Dictionary<uint, SharedFormula> sharedFormulas)
    {
        var values = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = CellReferences.ColumnIndex(cell.CellReference?.Value) ?? values.Count + 1;
            if (column <= values.Count)
            {
                continue;
            }

            while (values.Count < column - 1)
            {
                values.Add(string.Empty);
            }

            values.Add(cells.Read(cell, rowNumber, column, sharedFormulas));
        }

        var length = values.Count;
        while (length > 0 && values[length - 1].Length == 0)
        {
            length--;
        }

        values.RemoveRange(length, values.Count - length);
        return values;
    }
}

/// <summary>The text of a shared formula's anchor cell, reused (shifted) by the other cells sharing it.</summary>
internal sealed record SharedFormula(string Text, int Row, int Column);
