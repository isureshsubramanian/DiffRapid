using System.Diagnostics;
using System.Globalization;
using System.Text;
using DiffRapid.Core.Table;

namespace DiffRapid.Core.Tests;

public sealed class TableComparerTests
{
    private static TableData Csv(string text) => DelimitedReader.Parse(text, new DelimitedReadOptions { Delimiter = ',' });

    private static TableDiffResult Compare(string left, string right, TableCompareOptions? options = null) =>
        TableComparer.Compare(Csv(left), Csv(right), options);

    private static IEnumerable<TableRowStatus> Statuses(TableDiffResult result) => result.Rows.Select(r => r.Status);

    [Fact]
    public void Identical_tables_are_identical()
    {
        var result = Compare("id,name\n1,a\n2,b\n", "id,name\n1,a\n2,b");

        Assert.True(result.AreIdentical);
        Assert.All(result.Rows, r => Assert.Equal(TableRowStatus.Same, r.Status));
    }

    [Fact]
    public void Rows_are_aligned_by_position_with_changed_cells_and_inline_differences()
    {
        var result = Compare("id,name\n1,red apple\n2,b\n3,c\n", "id,name\n1,green apple\n3,c\n4,d\n");

        Assert.Equal([TableRowStatus.Changed, TableRowStatus.LeftOnly, TableRowStatus.Same, TableRowStatus.RightOnly], Statuses(result));
        var changed = result.Rows[0];
        var cell = Assert.Single(changed.Cells, c => c.IsChanged);
        Assert.Equal("name", result.Columns[cell.Column].Name);
        Assert.Equal(new Comparison.TextSpan(0, 3), Assert.Single(cell.Inline!.Left));
        Assert.Equal(new Comparison.TextSpan(0, 5), Assert.Single(cell.Inline.Right));
        Assert.False(result.AreIdentical);
    }

    [Fact]
    public void Rows_that_appear_elsewhere_are_reported_as_moved()
    {
        var result = Compare("k\na\nb\nc\nd\n", "k\nb\nc\nd\na\n");

        var moved = Assert.Single(result.Rows, r => r.Status == TableRowStatus.Moved);
        Assert.Equal(0, moved.LeftRow);
        Assert.Equal(3, moved.RightRow);
        Assert.Equal(4, result.Rows.Count);

        var plain = Compare("k\na\nb\nc\nd\n", "k\nb\nc\nd\na\n", new TableCompareOptions { DetectMovedRows = false });
        Assert.Contains(TableRowStatus.LeftOnly, Statuses(plain));
        Assert.Contains(TableRowStatus.RightOnly, Statuses(plain));
    }

    [Fact]
    public void Key_columns_match_rows_regardless_of_order()
    {
        var options = new TableCompareOptions { KeyColumns = ["id"] };
        var result = Compare("id,v\n1,a\n2,b\n3,c\n", "id,v\n2,b\n1,a\n3,changed\n4,new\n", options);

        // Either of the swapped rows may be the one reported as moved; the other stays in place.
        var swapped = result.Rows.Where(r => r.LeftRow is 0 or 1).Select(r => r.Status).OrderBy(s => s).ToList();
        Assert.Equal([TableRowStatus.Same, TableRowStatus.Moved], swapped);
        Assert.All(result.Rows.Where(r => r.LeftRow is 0 or 1), r => Assert.Equal(r.LeftRow, 1 - r.RightRow));
        Assert.Equal(TableRowStatus.Changed, Assert.Single(result.Rows, r => r.LeftRow == 2).Status);
        Assert.Equal(3, Assert.Single(result.Rows, r => r.Status == TableRowStatus.RightOnly).RightRow);
        Assert.True(result.Columns[0].IsKey);

        var unordered = Compare("id,v\n1,a\n2,b\n", "id,v\n2,b\n1,a\n", options with { DetectMovedRows = false });
        Assert.True(unordered.AreIdentical);
    }

    [Fact]
    public void Composite_keys_can_be_given_by_column_number()
    {
        var options = new TableCompareOptions { KeyColumns = ["1", "2"] };
        var result = Compare("a,b,v\nx,1,p\nx,2,q\n", "a,b,v\nx,2,q\ny,1,p\n", options);

        Assert.Equal(TableRowStatus.LeftOnly, Assert.Single(result.Rows, r => r.LeftRow == 0).Status);
        Assert.Contains(result.Rows, r => r.Status == TableRowStatus.RightOnly && r.RightRow == 1);
        Assert.Equal(TableRowStatus.Same, Assert.Single(result.Rows, r => r.LeftRow == 1).Status);
    }

    [Fact]
    public void Duplicate_keys_are_reported_and_matched_in_order()
    {
        var options = new TableCompareOptions { KeyColumns = ["id"] };
        var result = Compare("id,v\n1,a\n1,b\n2,c\n", "id,v\n1,a\n1,B\n1,extra\n2,c\n", options);

        var left = Assert.Single(result.DuplicateKeys, d => d.Side == TableSide.Left);
        Assert.Equal(["1"], left.Key);
        Assert.Equal([0, 1], left.Rows);
        Assert.Equal([0, 1, 2], Assert.Single(result.DuplicateKeys, d => d.Side == TableSide.Right).Rows);
        Assert.Equal(TableRowStatus.Changed, Assert.Single(result.Rows, r => r.LeftRow == 1).Status);
        Assert.Equal(2, Assert.Single(result.Rows, r => r.Status == TableRowStatus.RightOnly).RightRow);
    }

    [Fact]
    public void Columns_are_matched_by_header_name_when_their_order_differs()
    {
        var result = Compare("id,name,price\n1,a,10\n", "price,id,name\n10,1,a\n");

        Assert.True(result.AreIdentical);
        var byPosition = Compare("id,name,price\n1,a,10\n", "price,id,name\n10,1,a\n", new TableCompareOptions { MatchColumnsByName = false });
        Assert.False(byPosition.AreIdentical);
        Assert.Equal(3, byPosition.ColumnDifferences.Count());
    }

    [Fact]
    public void Columns_on_one_side_only_are_differences_unless_ignored()
    {
        var result = Compare("id,name\n1,a\n", "id,name,extra\n1,a,z\n");

        Assert.Equal(TableRowStatus.Same, Assert.Single(result.Rows).Status);
        var extra = Assert.Single(result.ColumnDifferences);
        Assert.Null(extra.LeftIndex);
        Assert.False(result.AreIdentical);
        Assert.True(Compare("id,name\n1,a\n", "id,name,extra\n1,a,z\n", new TableCompareOptions { IgnoreColumns = ["extra"] }).AreIdentical);
    }

    [Fact]
    public void Ignored_columns_are_not_compared()
    {
        var options = new TableCompareOptions { IgnoreColumns = ["updated"] };
        var result = Compare("id,updated\n1,2024-01-01\n", "id,updated\n1,2025-06-30\n", options);

        Assert.True(result.AreIdentical);
        Assert.False(Compare("id,updated\n1,2024-01-01\n", "id,updated\n1,2025-06-30\n").AreIdentical);
    }

    [Fact]
    public void Unknown_columns_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Compare("a\n1\n", "a\n1\n", new TableCompareOptions { KeyColumns = ["missing"] }));
        Assert.Throws<ArgumentException>(() => Compare("a\n1\n", "a,b\n1,2\n", new TableCompareOptions { KeyColumns = ["b"] }));
    }

    [Theory]
    [InlineData("1.000", "1.004", 0.005, 0, true)]
    [InlineData("1.000", "1.006", 0.005, 0, false)]
    [InlineData("1000", "1009", 0, 0.01, true)]
    [InlineData("1000", "1011", 0, 0.01, false)]
    [InlineData("1e3", "1000.0", 0.0001, 0, true)]
    [InlineData("1.0", "1", 0, 0, false)]
    [InlineData("abc", "abd", 1, 1, false)]
    [InlineData("1,5", "1,6", 1, 0, false)]
    public void Numeric_tolerance_is_absolute_or_relative_and_culture_invariant(string left, string right, double absolute, double relative, bool same)
    {
        var options = new TableCompareOptions { AbsoluteTolerance = absolute, RelativeTolerance = relative };
        var result = TableComparer.Compare(
            new TableData(null, [[left]]), new TableData(null, [[right]]), options);

        Assert.Equal(same, result.AreIdentical);
    }

    [Fact]
    public void Case_and_surrounding_whitespace_can_be_ignored()
    {
        Assert.False(Compare("n\nAbc \n", "n\nabc\n").AreIdentical);
        Assert.True(Compare("n\nAbc \n", "n\nabc\n", new TableCompareOptions { IgnoreCase = true, TrimWhitespace = true }).AreIdentical);
    }

    [Fact]
    public void Tables_without_headers_use_numbered_columns()
    {
        var options = new DelimitedReadOptions { Delimiter = ',', HasHeader = false };
        var result = TableComparer.Compare(DelimitedReader.Parse("1,a\n", options), DelimitedReader.Parse("1,b\n", options),
            new TableCompareOptions { KeyColumns = ["1"] });

        Assert.Equal("Column 2", result.Columns[1].Name);
        Assert.Equal(TableRowStatus.Changed, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void One_hundred_thousand_rows_compare_in_a_few_seconds()
    {
        var left = new StringBuilder("id,name,amount\n");
        var right = new StringBuilder("id,name,amount\n");
        for (var i = 0; i < 100_000; i++)
        {
            left.Append(i).Append(",name ").Append(i).Append(',').Append((i * 1.5).ToString(CultureInfo.InvariantCulture)).Append('\n');
            var amount = i % 1000 == 0 ? i * 1.5 + 1 : i * 1.5;
            right.Append(i).Append(",name ").Append(i).Append(',').Append(amount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var watch = Stopwatch.StartNew();
        var leftTable = Csv(left.ToString());
        var rightTable = Csv(right.ToString());
        var byPosition = TableComparer.Compare(leftTable, rightTable);
        var byKey = TableComparer.Compare(leftTable, rightTable, new TableCompareOptions { KeyColumns = ["id"] });
        watch.Stop();

        Assert.Equal(100, byPosition.Count(TableRowStatus.Changed));
        Assert.Equal(100, byKey.Count(TableRowStatus.Changed));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void Comparison_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => TableComparer.Compare(Csv("a\n1\n"), Csv("a\n2\n"), null, cancellation.Token));
    }

    [Fact]
    public void Markdown_report_lists_differences_and_escapes_cells()
    {
        var result = Compare("id,note\n1,\"a|b\"\n2,same\n3,gone\n", "id,note,extra\n1,\"a|c\"\n2,same\n", new TableCompareOptions { KeyColumns = ["id"] });
        using var writer = new StringWriter();
        TableReportWriter.WriteMarkdown(result, new TableReportOptions { LeftLabel = "old.csv", RightLabel = "new.csv" }, writer);
        var report = writer.ToString();

        Assert.Contains("- Left: old.csv", report);
        Assert.Contains("extra: right only", report);
        Assert.Contains("a\\|b -> a\\|c", report);
        Assert.Contains("| Left only | 4 |  | 3 | gone |", report);
        Assert.DoesNotContain("| same |", report);
    }

    [Fact]
    public void Html_report_highlights_changed_words_and_encodes_text()
    {
        var result = Compare("id,note\n1,<b> old\n", "id,note\n1,<b> new\n", new TableCompareOptions { KeyColumns = ["id"] });
        using var writer = new StringWriter();
        TableReportWriter.WriteHtml(result, new TableReportOptions { Title = "T&C" }, writer);
        var report = writer.ToString();

        Assert.Contains("<title>T&amp;C</title>", report);
        Assert.Contains("&lt;b&gt; <del>old</del> &rarr; &lt;b&gt; <ins>new</ins>", report);
        Assert.DoesNotContain("<b> old", report);
        Assert.Contains("prefers-color-scheme:dark", report);
    }

    [Fact]
    public void Duplicate_keys_appear_in_reports()
    {
        var result = Compare("id\n1\n1\n", "id\n1\n", new TableCompareOptions { KeyColumns = ["id"] });
        using var writer = new StringWriter();
        TableReportWriter.WriteMarkdown(result, new TableReportOptions(), writer);

        Assert.Contains("Left key \\[1\\] on lines 2, 3", writer.ToString());
    }
}

public sealed class TableCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-table-cli-" + Guid.NewGuid());

    public TableCliTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var cli = typeof(DiffRapid.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("table");
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    [Fact]
    public void Table_command_reports_differences_with_exit_codes()
    {
        var left = Write("left.csv", "id;name;price\n1;a;10\n2;b;20\n");
        var right = Write("right.csv", "id;price;name\n2;20.4;b\n1;10;a\n");

        Assert.Equal(0, Run(left, left).Code);
        var (code, output) = Run(left, right, "--key", "id");
        Assert.Equal(1, code);
        Assert.Contains("price: \"20\" -> \"20.4\"", output);
        Assert.Equal(1, Run(left, right, "--key", "id", "--tolerance", "0.5").Code);
        Assert.Equal(0, Run(left, right, "--key", "id", "--tolerance", "0.5", "--ignore-order").Code);
        Assert.Equal(2, Run(left, right, "--ignore-order").Code);
        Assert.Equal(0, Run(left, right, "--key=id", "--ignore-column", "price", "--delimiter", ";", "--ignore-order").Code);
        Assert.Equal(2, Run(left, right, "--key", "missing").Code);
        Assert.Equal(2, Run(left, right, "--bogus").Code);
        Assert.Equal(2, Run(left).Code);
    }

    [Theory]
    [InlineData("md", "## Rows")]
    [InlineData("html", "<!doctype html>")]
    public void Table_command_writes_reports(string format, string expected)
    {
        var left = Write("left.tsv", "id\tv\n1\tx\n");
        var right = Write("right.tsv", "id\tv\n1\ty\n");
        var report = Path.Combine(_root, "report." + format);

        var (code, _) = Run(left, right, "--report", format, "-o", report);

        Assert.Equal(1, code);
        Assert.Contains(expected, File.ReadAllText(report));
    }
}
