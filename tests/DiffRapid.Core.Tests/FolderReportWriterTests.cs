using System.Globalization;
using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class FolderReportWriterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reports_encode_filter_sort_and_ignore_culture(bool html)
    {
        var meta = new FileMeta(1234567, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 0, true);
        FolderCompareItem Item(string path, FolderCompareStatus status, FolderItemKind kind = FolderItemKind.File) =>
            new(path, kind, status, meta, meta, 1234, "error <&\"", Array.Empty<FolderCompareItem>());
        var items = new[] { Item("z,\"\r\n<&>", FolderCompareStatus.Error), Item("A-file", FolderCompareStatus.LeftOnly),
            Item("same-file", FolderCompareStatus.Identical), Item("skip-file", FolderCompareStatus.Skipped),
            Item("aggregate-folder", FolderCompareStatus.Different, FolderItemKind.Folder),
            Item("only-folder", FolderCompareStatus.RightOnly, FolderItemKind.Folder),
            Item("conflict-folder", FolderCompareStatus.TypeConflict, FolderItemKind.Folder),
            Item("error-folder", FolderCompareStatus.Error, FolderItemKind.Folder) };
        var result = new FolderCompareResult(items[0], items, new Dictionary<FolderCompareStatus, int>());
        string Render(bool include = false)
        {
            using var writer = new StringWriter();
            var options = new FolderReportOptions { LeftRoot = "left<&\"", RightRoot = "right>", Title = "title<&\"",
                MethodLabel = "method<&\"", GeneratedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), IncludeIdentical = include };
            if (html) FolderReportWriter.WriteHtml(result, options, writer);
            else FolderReportWriter.WriteCsv(result, options, writer);
            return writer.ToString();
        }
        var baseline = Render();
        Assert.DoesNotContain("same-file", baseline);
        Assert.DoesNotContain("skip-file", Render(true));
        Assert.Contains("same-file", Render(true));
        Assert.DoesNotContain("aggregate-folder", baseline);
        foreach (var path in new[] { "only-folder", "conflict-folder", "error-folder" }) Assert.Contains(path, baseline);
        Assert.True(baseline.IndexOf("A-file", StringComparison.Ordinal) < baseline.IndexOf("z,", StringComparison.Ordinal));
        if (html)
        {
            foreach (var text in new[] { "&lt;&amp;&gt;", "left&lt;&amp;&quot;", "right&gt;", "title&lt;&amp;&quot;", "method&lt;&amp;&quot;", "error &lt;&amp;&quot;", "2026-01-02T01:04:05" }) Assert.Contains(text, baseline);
            Assert.DoesNotContain("<&>", baseline);
            Assert.Contains("prefers-color-scheme:dark", baseline);
        }
        else
        {
            Assert.StartsWith("Path,Status,LeftSize,LeftModifiedUtc,RightSize,RightModifiedUtc,Differences,Error\r\n", baseline);
            Assert.Contains("\"z,\"\"\r\n<&>\"", baseline);
            Assert.Contains("\"2026-01-02T03:04:05.0000000Z\"", baseline);
            Assert.Contains("\"1234567\"", baseline);
        }
        var original = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Assert.Equal(baseline, Render()); }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
