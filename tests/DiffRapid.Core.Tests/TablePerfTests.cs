using DiffRapid.Core.Table;

namespace DiffRapid.Core.Tests;

/// <summary>Report cell lookup by column index, delimiter detection on a sample, and cancellable parsing.</summary>
public sealed class TablePerfTests
{
    [Fact]
    public void ReportsPlaceChangedCellsUnderTheirColumnPastIgnoredOnes()
    {
        var left = Csv("id,skip,a,b\n1,x,old,same\n");
        var right = Csv("id,skip,a,b\n1,y,new,same\n");
        var result = TableComparer.Compare(left, right, new TableCompareOptions { KeyColumns = ["id"], IgnoreColumns = ["skip"] }, TestContext.Current.CancellationToken);

        using var markdown = new StringWriter();
        TableReportWriter.WriteMarkdown(result, new TableReportOptions(), markdown);
        Assert.Contains("| id | a | b |", markdown.ToString(), StringComparison.Ordinal);
        Assert.Contains("| 1 | old -> new | same |", markdown.ToString(), StringComparison.Ordinal);

        using var html = new StringWriter();
        TableReportWriter.WriteHtml(result, new TableReportOptions(), html);
        Assert.Contains("<td>1</td><td class=\"c\"><del>old</del> &rarr; <ins>new</ins></td><td>same</td>", html.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DelimiterDetectionLooksOnlyAtTheStartOfTheText()
    {
        var head = string.Concat(Enumerable.Repeat("a;b;c\n", 30));
        var tail = string.Concat(Enumerable.Repeat("x,y,z,w,v,u\n", 200_000));
        Assert.Equal(';', DelimitedReader.DetectDelimiter(head + tail));

        // An unclosed quote no longer makes detection scan the whole text once per candidate.
        var unclosed = "\"" + new string('a', 10_000_000);
        Assert.Equal(',', DelimitedReader.DetectDelimiter(unclosed));
    }

    [Fact]
    public void LargeFirstLinesAreCutAtTheirLastLineBreak()
    {
        var wide = string.Join("\t", Enumerable.Range(0, 20_000).Select(index => "c" + index)) + "\n";
        Assert.Equal('\t', DelimitedReader.DetectDelimiter(wide + wide + wide));
    }

    [Fact]
    public void ParseHonoursCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => DelimitedReader.Parse("a,b\n1,2\n", null, source.Token));
    }

    private static TableData Csv(string text) => DelimitedReader.Parse(text, new DelimitedReadOptions { Delimiter = ',' });
}
