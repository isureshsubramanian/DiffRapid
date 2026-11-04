using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Table;

/// <summary>Caller-supplied labels for a table report.</summary>
public sealed record TableReportOptions
{
    public string Title { get; init; } = "Table comparison";

    public string LeftLabel { get; init; } = "Left";

    public string RightLabel { get; init; } = "Right";
}

/// <summary>
/// Writes the differences of a table comparison as Markdown or as a self-contained HTML page. Rows that are
/// the same are left out; changed cells show the left and the right text.
/// </summary>
public static class TableReportWriter
{
    private const string Style =
        "body{font:14px/1.45 system-ui,'Segoe UI',sans-serif;margin:2rem;color:#1f2328;background:#fff}"
        + "h1{font-size:1.5rem;margin:0 0 .75rem}h2{font-size:1.15rem;margin:1.5rem 0 .5rem}"
        + "dl{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem;margin:0 0 1.25rem}dt{color:#656d76}dd{margin:0;overflow-wrap:anywhere}"
        + ".summary{display:flex;flex-wrap:wrap;gap:.6rem;margin:0 0 1.25rem;padding:0;list-style:none}"
        + ".summary li{border:1px solid #d0d7de;border-radius:6px;padding:.4rem .8rem}.summary b{font-size:1.15rem;margin-right:.35rem}"
        + "table{border-collapse:collapse}th,td{padding:.3rem .55rem;border:1px solid #d0d7de;text-align:left;vertical-align:top}"
        + "th{font-weight:600;background:#f6f8fa}td{white-space:pre-wrap;overflow-wrap:anywhere}td.n{text-align:right}"
        + "del{background:#ffd7d5;text-decoration:none}ins{background:#ccffd8;text-decoration:none}.c{background:#fff8c5}"
        + ".Changed{color:#7d4e00}.LeftOnly{color:#a40e26}.RightOnly{color:#0b5a2b}.Moved{color:#5a32a3}"
        + "@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}dt{color:#8d96a0}th{background:#161b22}"
        + "th,td,.summary li{border-color:#30363d}del{background:#5d1f1f}ins{background:#1b4b2c}.c{background:#3b2e05}"
        + ".Changed{color:#f2cc60}.LeftOnly{color:#ff9b9b}.RightOnly{color:#7ee2a0}.Moved{color:#d2b8ff}}";

    private static readonly TableRowStatus[] SummaryStatuses =
    [
        TableRowStatus.Same, TableRowStatus.Changed, TableRowStatus.LeftOnly, TableRowStatus.RightOnly, TableRowStatus.Moved,
    ];

    /// <summary>Writes a GitHub-flavoured Markdown report.</summary>
    public static void WriteMarkdown(TableDiffResult result, TableReportOptions options, TextWriter writer)
    {
        Validate(result, options, writer);
        var columns = ShownColumns(result);
        writer.WriteLine("# " + Markdown(options.Title));
        writer.WriteLine();
        writer.WriteLine("- Left: " + Markdown(options.LeftLabel));
        writer.WriteLine("- Right: " + Markdown(options.RightLabel));
        writer.WriteLine("- Rows: " + string.Join(", ", SummaryStatuses.Select(s => Count(result, s) + " " + Label(s).ToLowerInvariant())));
        writer.WriteLine();
        WriteMarkdownColumns(result, writer);
        WriteMarkdownDuplicates(result, writer);

        var rows = result.Rows.Where(r => r.Status != TableRowStatus.Same).ToList();
        if (rows.Count == 0)
        {
            writer.WriteLine("No row differences.");
            return;
        }

        writer.WriteLine("## Rows");
        writer.WriteLine();
        writer.WriteLine("| Status | Left line | Right line | " + string.Join(" | ", columns.Select(c => Markdown(c.Name))) + " |");
        writer.WriteLine("|---|---:|---:|" + string.Concat(columns.Select(_ => "---|")));
        foreach (var row in rows)
        {
            var cells = columns.Select(c => MarkdownCell(result, row, c));
            writer.WriteLine("| " + Label(row.Status) + " | " + Line(result.Left, row.LeftRow) + " | " + Line(result.Right, row.RightRow)
                + " | " + string.Join(" | ", cells) + " |");
        }
    }

    /// <summary>Writes one self-contained HTML page (inline styles, no scripts) that follows light and dark mode.</summary>
    public static void WriteHtml(TableDiffResult result, TableReportOptions options, TextWriter writer)
    {
        Validate(result, options, writer);
        writer.Write("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>");
        writer.Write(Html(options.Title));
        writer.Write("</title><style>" + Style + "</style></head><body><h1>" + Html(options.Title) + "</h1><dl>");
        writer.Write("<dt>Left</dt><dd>" + Html(options.LeftLabel) + "</dd><dt>Right</dt><dd>" + Html(options.RightLabel) + "</dd></dl>");
        writer.Write("<ul class=\"summary\">");
        foreach (var status in SummaryStatuses)
        {
            writer.Write("<li><b>" + Count(result, status) + "</b>" + Label(status) + "</li>");
        }

        writer.Write("</ul>");
        WriteHtmlColumns(result, writer);
        WriteHtmlDuplicates(result, writer);
        WriteHtmlRows(result, writer);
        writer.Write("</body></html>");
    }

    private static void WriteMarkdownColumns(TableDiffResult result, TextWriter writer)
    {
        var differences = result.ColumnDifferences.ToList();
        if (differences.Count == 0)
        {
            return;
        }

        writer.WriteLine("## Columns");
        writer.WriteLine();
        foreach (var column in differences)
        {
            writer.WriteLine("- " + Markdown(ColumnText(column)));
        }

        writer.WriteLine();
    }

    private static void WriteMarkdownDuplicates(TableDiffResult result, TextWriter writer)
    {
        if (result.DuplicateKeys.Count == 0)
        {
            return;
        }

        writer.WriteLine("## Duplicate keys");
        writer.WriteLine();
        foreach (var duplicate in result.DuplicateKeys)
        {
            writer.WriteLine("- " + Markdown(DuplicateText(result, duplicate)));
        }

        writer.WriteLine();
    }

    private static void WriteHtmlColumns(TableDiffResult result, TextWriter writer)
    {
        var differences = result.ColumnDifferences.ToList();
        if (differences.Count == 0)
        {
            return;
        }

        writer.Write("<h2>Columns</h2><ul>");
        foreach (var column in differences)
        {
            writer.Write("<li>" + Html(ColumnText(column)) + "</li>");
        }

        writer.Write("</ul>");
    }

    private static void WriteHtmlDuplicates(TableDiffResult result, TextWriter writer)
    {
        if (result.DuplicateKeys.Count == 0)
        {
            return;
        }

        writer.Write("<h2>Duplicate keys</h2><ul>");
        foreach (var duplicate in result.DuplicateKeys)
        {
            writer.Write("<li>" + Html(DuplicateText(result, duplicate)) + "</li>");
        }

        writer.Write("</ul>");
    }

    private static void WriteHtmlRows(TableDiffResult result, TextWriter writer)
    {
        var rows = result.Rows.Where(r => r.Status != TableRowStatus.Same).ToList();
        if (rows.Count == 0)
        {
            writer.Write("<p>No row differences.</p>");
            return;
        }

        var columns = ShownColumns(result);
        writer.Write("<h2>Rows</h2><table><thead><tr><th>Status</th><th>Left line</th><th>Right line</th>");
        foreach (var column in columns)
        {
            writer.Write("<th>" + Html(column.Name) + "</th>");
        }

        writer.Write("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            writer.Write("<tr><td class=\"" + row.Status + "\">" + Label(row.Status) + "</td>");
            writer.Write("<td class=\"n\">" + Line(result.Left, row.LeftRow) + "</td><td class=\"n\">" + Line(result.Right, row.RightRow) + "</td>");
            foreach (var column in columns)
            {
                writer.Write(HtmlCell(result, row, column));
            }

            writer.Write("</tr>");
        }

        writer.Write("</tbody></table>");
    }

    private static List<TableColumn> ShownColumns(TableDiffResult result) => result.Columns.Where(c => !c.IsIgnored).ToList();

    private static string MarkdownCell(TableDiffResult result, TableRowDiff row, TableColumn column)
    {
        if (ChangedCell(result, row, column) is { } cell)
        {
            return Markdown(cell.Left) + " -> " + Markdown(cell.Right);
        }

        return Markdown(CellText(result, row, column));
    }

    private static string HtmlCell(TableDiffResult result, TableRowDiff row, TableColumn column)
    {
        if (ChangedCell(result, row, column) is not { } cell)
        {
            return "<td>" + Html(CellText(result, row, column)) + "</td>";
        }

        var left = Highlight(cell.Left, cell.Inline?.Left, "del");
        var right = Highlight(cell.Right, cell.Inline?.Right, "ins");
        return "<td class=\"c\">" + left + " &rarr; " + right + "</td>";
    }

    // Wraps the changed ranges of a cell in the given tag.
    private static string Highlight(string text, IReadOnlyList<TextSpan>? spans, string tag)
    {
        if (spans is null || spans.Count == 0)
        {
            return "<" + tag + ">" + Html(text) + "</" + tag + ">";
        }

        var builder = new StringBuilder();
        var position = 0;
        foreach (var span in spans)
        {
            builder.Append(Html(text.Substring(position, span.Start - position)));
            builder.Append('<').Append(tag).Append('>').Append(Html(text.Substring(span.Start, span.Length))).Append("</").Append(tag).Append('>');
            position = span.End;
        }

        builder.Append(Html(text.Substring(position)));
        return builder.ToString();
    }

    private static TableCellDiff? ChangedCell(TableDiffResult result, TableRowDiff row, TableColumn column)
    {
        var index = IndexOf(result, column);
        return row.Cells.FirstOrDefault(c => c.Column == index && c.IsChanged);
    }

    private static int IndexOf(TableDiffResult result, TableColumn column)
    {
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (ReferenceEquals(result.Columns[i], column))
            {
                return i;
            }
        }

        return -1;
    }

    // Shows the left text for rows on the left, otherwise the right text.
    private static string CellText(TableDiffResult result, TableRowDiff row, TableColumn column)
    {
        if (row.LeftRow is { } leftRow && column.LeftIndex is { } leftColumn)
        {
            return result.Left.Cell(leftRow, leftColumn);
        }

        if (row.RightRow is { } rightRow && column.RightIndex is { } rightColumn)
        {
            return result.Right.Cell(rightRow, rightColumn);
        }

        return string.Empty;
    }

    private static string ColumnText(TableColumn column)
    {
        if (column.LeftIndex is null)
        {
            return column.Name + ": right only";
        }

        if (column.RightIndex is null)
        {
            return column.Name + ": left only";
        }

        return column.LeftName + ": renamed to " + column.RightName;
    }

    private static string DuplicateText(TableDiffResult result, TableDuplicateKey duplicate)
    {
        var table = duplicate.Side == TableSide.Left ? result.Left : result.Right;
        var lines = string.Join(", ", duplicate.Rows.Select(r => table.LineOf(r).ToString(CultureInfo.InvariantCulture)));
        return duplicate.Side + " key [" + string.Join(", ", duplicate.Key) + "] on lines " + lines;
    }

    private static string Count(TableDiffResult result, TableRowStatus status) =>
        result.Count(status).ToString(CultureInfo.InvariantCulture);

    private static string Line(TableData table, int? row) =>
        row is { } r ? table.LineOf(r).ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Label(TableRowStatus status) => status switch
    {
        TableRowStatus.Same => "Same",
        TableRowStatus.Changed => "Changed",
        TableRowStatus.LeftOnly => "Left only",
        TableRowStatus.RightOnly => "Right only",
        TableRowStatus.Moved => "Moved",
        _ => status.ToString(),
    };

    private static string Html(string text) => WebUtility.HtmlEncode(text).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    private static string Markdown(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                if (crlf)
                {
                    i++;
                }

                builder.Append("<br>");
            }
            else if (c == '\n')
            {
                builder.Append("<br>");
            }
            else if (c == '<')
            {
                builder.Append("&lt;");
            }
            else if (c == '>')
            {
                builder.Append("&gt;");
            }
            else if (c == '&')
            {
                builder.Append("&amp;");
            }
            else if ("\\|*_`[]#~".IndexOf(c) >= 0)
            {
                builder.Append('\\').Append(c);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void Validate(TableDiffResult result, TableReportOptions options, TextWriter writer)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }
    }
}
