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
    private static readonly HashSet<string> TableValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "delimiter", "quote", "key", "ignore-column", "tolerance", "relative-tolerance", "report", "output",
    };

    private static readonly HashSet<string> TableSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-header", "ignore-case", "trim", "by-position", "ignore-order", "quiet",
    };

    /// <summary>Compares two delimited text files as tables. Parses its own arguments from the raw command line.</summary>
    private static int RunTable(string[] args)
    {
        var (positionals, values, switches) = ParseTableArguments(args);
        if (positionals.Count != 2)
        {
            throw new UsageException("table needs two files.");
        }

        var readOptions = new DelimitedReadOptions
        {
            Delimiter = TableDelimiter(Last(values, "delimiter")),
            Quote = Last(values, "quote") is { } quote ? SingleCharacter(quote, "--quote") : '"',
            HasHeader = !switches.Contains("no-header"),
        };
        var left = DelimitedReader.ReadFile(positionals[0], readOptions);
        var right = DelimitedReader.ReadFile(positionals[1], readOptions);
        var compareOptions = new TableCompareOptions
        {
            KeyColumns = SplitColumns(values, "key"),
            IgnoreColumns = SplitColumns(values, "ignore-column"),
            IgnoreCase = switches.Contains("ignore-case"),
            TrimWhitespace = switches.Contains("trim"),
            AbsoluteTolerance = Number(Last(values, "tolerance"), "--tolerance"),
            RelativeTolerance = Number(Last(values, "relative-tolerance"), "--relative-tolerance"),
            MatchColumnsByName = !switches.Contains("by-position"),
            DetectMovedRows = !switches.Contains("ignore-order"),
        };
        if (switches.Contains("ignore-order") && compareOptions.KeyColumns.Count == 0)
        {
            throw new UsageException("--ignore-order needs --key.");
        }

        var result = TableComparer.Compare(left, right, compareOptions);
        var exitCode = result.AreIdentical ? ExitSame : ExitDifferent;
        if (Last(values, "report") is { } format)
        {
            WriteTableReport(result, format, Last(values, "output"), positionals);
            return exitCode;
        }

        if (!switches.Contains("quiet"))
        {
            WriteTableSummary(result, Console.Out);
        }

        return exitCode;
    }

    private static (List<string> Positionals, Dictionary<string, List<string>> Values, HashSet<string> Switches) ParseTableArguments(string[] args)
    {
        var positionals = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            var isOption = argument.StartsWith("--", StringComparison.Ordinal) || argument == "-o";
            if (!isOption)
            {
                positionals.Add(argument);
                continue;
            }

            var name = argument == "-o" ? "output" : argument.Substring(2);
            string? value = null;
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                value = name.Substring(equals + 1);
                name = name.Substring(0, equals);
            }

            if (TableSwitches.Contains(name) && value is null)
            {
                switches.Add(name);
                continue;
            }

            if (!TableValueOptions.Contains(name))
            {
                throw new UsageException($"Unknown table option '{argument}'.");
            }

            if (value is null)
            {
                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"Option '{argument}' needs a value.");
                }

                value = args[++i];
            }

            if (!values.TryGetValue(name, out var list))
            {
                list = [];
                values[name] = list;
            }

            list.Add(value);
        }

        return (positionals, values, switches);
    }

    private static string? Last(Dictionary<string, List<string>> values, string name) =>
        values.TryGetValue(name, out var list) ? list[list.Count - 1] : null;

    // Every occurrence counts and each may hold a comma-separated list.
    private static IReadOnlyList<string> SplitColumns(Dictionary<string, List<string>> values, string name)
    {
        if (!values.TryGetValue(name, out var list))
        {
            return Array.Empty<string>();
        }

        return list.SelectMany(v => v.Split(',')).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
    }

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
        using var buffer = new StringWriter();
        switch (format.ToLowerInvariant())
        {
            case "md":
            case "markdown":
                TableReportWriter.WriteMarkdown(result, options, buffer);
                break;
            case "html":
            case "htm":
                TableReportWriter.WriteHtml(result, options, buffer);
                break;
            default:
                throw new UsageException($"Unknown report format '{format}'; use md or html.");
        }

        if (output is null)
        {
            Console.Out.Write(buffer.ToString());
            return;
        }

        File.WriteAllText(output, buffer.ToString(), new UTF8Encoding(false));
    }

    private static void WriteTableSummary(TableDiffResult result, TextWriter writer)
    {
        foreach (var column in result.ColumnDifferences)
        {
            var side = column.LeftIndex is null ? "right only" : column.RightIndex is null ? "left only" : "renamed to " + column.RightName;
            writer.WriteLine($"Column {column.Name}: {side}");
        }

        foreach (var duplicate in result.DuplicateKeys)
        {
            var table = duplicate.Side == TableSide.Left ? result.Left : result.Right;
            var lines = string.Join(", ", duplicate.Rows.Select(table.LineOf));
            writer.WriteLine($"Duplicate {duplicate.Side.ToString().ToLowerInvariant()} key [{string.Join(", ", duplicate.Key)}] on lines {lines}");
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
            writer.WriteLine($"  {result.Columns[cell.Column].Name}: \"{cell.Left}\" -> \"{cell.Right}\"");
        }
    }
}
