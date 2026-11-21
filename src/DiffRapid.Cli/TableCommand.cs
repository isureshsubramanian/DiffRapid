using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DiffRapid.Core.Table;

namespace DiffRapid.Cli;

internal static partial class Program
{
    /// <summary>Compares two delimited text files as tables.</summary>
    private static int RunTable(ParsedArgs args)
    {
        var positionals = args.Positionals;
        if (positionals.Count != 2)
        {
            throw new UsageException("table needs two files.");
        }

        var readOptions = new DelimitedReadOptions
        {
            Delimiter = TableDelimiter(args.Value("delimiter")),
            Quote = args.Value("quote") is { } quote ? SingleCharacter(quote, "--quote") : '"',
            HasHeader = !args.Has("no-header"),
        };
        var left = DelimitedReader.ReadFile(positionals[0], readOptions);
        var right = DelimitedReader.ReadFile(positionals[1], readOptions);
        var compareOptions = new TableCompareOptions
        {
            KeyColumns = SplitColumns(args, "key"),
            IgnoreColumns = SplitColumns(args, "ignore-column"),
            IgnoreCase = args.Has("ignore-case"),
            TrimWhitespace = args.Has("trim"),
            AbsoluteTolerance = Number(args.Value("tolerance"), "--tolerance"),
            RelativeTolerance = Number(args.Value("relative-tolerance"), "--relative-tolerance"),
            MatchColumnsByName = !args.Has("by-position"),
            DetectMovedRows = !args.Has("ignore-order"),
        };
        if (args.Has("ignore-order") && compareOptions.KeyColumns.Count == 0)
        {
            throw new UsageException("--ignore-order needs --key.");
        }

        var result = TableComparer.Compare(left, right, compareOptions);
        var exitCode = result.AreIdentical ? ExitSame : ExitDifferent;
        if (args.Value("report") is { } format)
        {
            WriteTableReport(result, format, args.Value("output"), positionals);
            return exitCode;
        }

        if (!args.Has("quiet"))
        {
            WriteTableSummary(result, Console.Out);
        }

        return exitCode;
    }

    // Every occurrence counts and each may hold a comma-separated list.
    private static IReadOnlyList<string> SplitColumns(ParsedArgs args, string name) =>
        args.Values(name).SelectMany(v => v.Split(',')).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    private static char? TableDelimiter(string? value) => value?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "tab" or "\\t" => '\t',
        "comma" => ',',
        "semicolon" => ';',
        "pipe" => '|',
        "space" => ' ',
        _ => SingleCharacter(value, "--delimiter"),
    };

    private static char SingleCharacter(string value, string option)
    {
        if (value.Length != 1)
        {
            throw new UsageException($"{option} needs a single character, not '{value}'.");
        }

        return value[0];
    }

    private static double Number(string? value, string option)
    {
        if (value is null)
        {
            return 0;
        }

        var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
        if (!parsed || number < 0 || double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new UsageException($"'{value}' is not a valid value for {option}.");
        }

        return number;
    }

    private static void WriteTableReport(TableDiffResult result, string format, string? output, List<string> positionals)
    {
        var options = new TableReportOptions { LeftLabel = positionals[0], RightLabel = positionals[1] };
        Action<TextWriter> write = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => writer => TableReportWriter.WriteMarkdown(result, options, writer),
            "html" or "htm" => writer => TableReportWriter.WriteHtml(result, options, writer),
            _ => throw new UsageException($"Unknown report format '{format}'; use md or html."),
        };
        WriteText(output, write);
    }

    private static void WriteTableSummary(TableDiffResult result, TextWriter writer)
    {
        foreach (var column in result.ColumnDifferences)
        {
            var side = column.LeftIndex is null ? "right only" : column.RightIndex is null ? "left only" : "renamed to " + column.RightName;
            writer.WriteLine(TerminalText.Escape($"Column {column.Name}: {side}"));
        }

        foreach (var duplicate in result.DuplicateKeys)
        {
            var table = duplicate.Side == TableSide.Left ? result.Left : result.Right;
            var lines = string.Join(", ", duplicate.Rows.Select(table.LineOf));
            writer.WriteLine(TerminalText.Escape($"Duplicate {duplicate.Side.ToString().ToLowerInvariant()} key [{string.Join(", ", duplicate.Key)}] on lines {lines}"));
        }

        foreach (var row in result.Rows.Where(r => r.Status != TableRowStatus.Same))
        {
            WriteTableRow(result, row, writer);
        }

        var counts = Enum.GetValues(typeof(TableRowStatus)).Cast<TableRowStatus>().Select(s => $"{s}: {result.Count(s)}");
        writer.WriteLine(string.Join(", ", counts));
    }

    private static void WriteTableRow(TableDiffResult result, TableRowDiff row, TextWriter writer)
    {
        var leftLine = row.LeftRow is { } leftRow ? result.Left.LineOf(leftRow).ToString(CultureInfo.InvariantCulture) : "-";
        var rightLine = row.RightRow is { } rightRow ? result.Right.LineOf(rightRow).ToString(CultureInfo.InvariantCulture) : "-";
        writer.WriteLine($"{row.Status} {leftLine}/{rightLine}");
        foreach (var cell in row.Cells.Where(c => c.IsChanged))
        {
            writer.WriteLine(TerminalText.Escape($"  {result.Columns[cell.Column].Name}: \"{cell.Left}\" -> \"{cell.Right}\""));
        }
    }
}
