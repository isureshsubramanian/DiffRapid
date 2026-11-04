using System.Text;
using System.Text.Json;
using DiffRapid.Cli;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-report-" + Guid.NewGuid());

    public ReportTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static TextDiffResult Compare(string left, string right, CompareOptions? options = null) =>
        TextComparer.Compare(TextDocument.Parse(left), TextDocument.Parse(right), options);

    private static string Html(TextDiffResult result, FileReportOptions? options = null)
    {
        using var writer = new StringWriter();
        FileReportWriter.WriteHtml(result, options ?? new FileReportOptions(), writer);
        return writer.ToString();
    }

    private static string Markdown(TextDiffResult result, FileReportOptions? options = null)
    {
        using var writer = new StringWriter();
        FileReportWriter.WriteMarkdown(result, options ?? new FileReportOptions(), writer);
        return writer.ToString();
    }

    private static JsonDocument FileJson(TextDiffResult result, FileReportOptions? options = null)
    {
        using var stream = new MemoryStream();
        JsonReportWriter.WriteFile(result, options ?? new FileReportOptions(), stream);
        return JsonDocument.Parse(stream.ToArray());
    }

    [Fact]
    public void Html_escapes_text_labels_and_title()
    {
        var result = Compare("keep\nif (a < b && c) { x = \"old\"; }\n", "keep\nif (a < b && c) { x = \"new\"; }\n<script>alert(1)</script>\n");
        var html = Html(result, new FileReportOptions { Title = "T<i>&", LeftLabel = "a<b>.txt", RightLabel = "c\"d\".txt" });

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("if (a &lt; b &amp;&amp; c)", html);
        Assert.Contains("T&lt;i&gt;&amp;", html);
        Assert.Contains("a&lt;b&gt;.txt", html);
        Assert.Contains("c&quot;d&quot;.txt", html);
        Assert.Contains("<mark>old</mark>", html);
        Assert.Contains("<mark>new</mark>", html);
        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.StartsWith("<!doctype html>", html);
        Assert.EndsWith("</body></html>", html);
    }

    [Fact]
    public void Html_shows_added_and_removed_lines_against_empty_cells()
    {
        var html = Html(Compare("a\nremoved\nb\n", "a\nb\nadded\n"));

        Assert.Contains("<tr class=\"del\"><td class=\"ln\">2</td><td class=\"tx o\">removed</td><td class=\"ln\"></td><td class=\"tx e\"></td></tr>", html);
        Assert.Contains("<tr class=\"add\"><td class=\"ln\"></td><td class=\"tx e\"></td><td class=\"ln\">3</td><td class=\"tx n\">added</td></tr>", html);
        Assert.Contains("2 differences: 0 changed, 1 added, 1 removed lines.", html);
    }

    [Fact]
    public void Html_collapses_unchanged_runs_in_large_files()
    {
        var lines = Enumerable.Range(1, 200_000).Select(i => "line " + i).ToArray();
        var left = string.Join("\n", lines) + "\n";
        lines[99_999] = "changed";
        var right = string.Join("\n", lines) + "\n";

        var html = Html(Compare(left, right), new FileReportOptions { ContextLines = 3 });

        Assert.Contains("99996 unchanged lines", html);
        Assert.Contains("99997 unchanged lines", html);
        Assert.Contains("<td class=\"ln\">100000</td><td class=\"tx o\"><mark>line 100000</mark></td>", html);
        Assert.Contains(">99997<", html);
        Assert.DoesNotContain(">99996<", html);
        Assert.True(html.Length < 20_000, $"Report is {html.Length} characters.");
    }

    [Fact]
    public void Html_of_identical_files_is_one_collapsed_row_and_context_can_show_all()
    {
        var text = "a\nb\nc\n";
        Assert.Contains("3 unchanged lines", Html(Compare(text, text)));
        Assert.Contains("The files are identical.", Html(Compare(text, text)));

        var all = Html(Compare("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", "1\n2\n3\n4\n5\nX\n7\n8\n9\n10\n"), new FileReportOptions { ContextLines = int.MaxValue });
        Assert.DoesNotContain("unchanged line", all);
        Assert.Contains(">10<", all);
    }

    [Fact]
    public void Html_shows_ignored_differences_muted_and_rejects_negative_context()
    {
        var html = Html(Compare("a\n\nb\n", "a\nb\n", CompareOptions.Default with { IgnoreBlankLines = true }));
        Assert.Contains("<tr class=\"ign\">", html);
        Assert.Contains("The files are identical.", html);

        Assert.Throws<ArgumentOutOfRangeException>(() => Html(Compare("a", "b"), new FileReportOptions { ContextLines = -1 }));
    }

    [Fact]
    public void Markdown_fence_is_longer_than_backticks_in_the_content()
    {
        var result = Compare("```\ncode ```` here\nold\n", "```\ncode ```` here\nnew\n");
        var markdown = Markdown(result, new FileReportOptions { Title = "Fix *bold* #1", LeftLabel = "a`b.txt", RightLabel = "b.txt" });

        Assert.StartsWith("## Fix \\*bold\\* \\#1\n", markdown);
        Assert.Contains("**Left:** ``a`b.txt``", markdown);
        Assert.Contains("**Right:** `b.txt`", markdown);
        Assert.Contains("\n`````diff\n--- a`b.txt\n+++ b.txt\n", markdown);
        Assert.Contains(" ```\n code ```` here\n-old\n+new\n`````\n", markdown);
        Assert.EndsWith("`````\n", markdown);
    }

    [Fact]
    public void Markdown_uses_three_backticks_by_default_and_no_fence_when_identical()
    {
        var different = Markdown(Compare("a\n", "b\n"));
        Assert.Contains("1 difference: 1 changed, 0 added, 0 removed lines.\n\n```diff\n", different);
        Assert.EndsWith("+b\n```\n", different);

        var identical = Markdown(Compare("a\n", "a\n"));
        Assert.Contains("The files are identical.", identical);
        Assert.DoesNotContain("```", identical);
    }

    [Fact]
    public void Json_for_a_file_has_the_documented_shape()
    {
        var result = Compare("a\nold\nb\nremoved\n", "a\nnew\nmore\nb\n");
        using var json = FileJson(result, new FileReportOptions { LeftLabel = "left.txt", RightLabel = "right <&>.txt" });
        var root = json.RootElement;

        Assert.Equal(JsonReportWriter.SchemaVersion, root.GetProperty("version").GetInt32());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("file", root.GetProperty("kind").GetString());
        Assert.Equal("left.txt", root.GetProperty("left").GetProperty("label").GetString());
        Assert.Equal("right <&>.txt", root.GetProperty("right").GetProperty("label").GetString());
        Assert.Equal(4, root.GetProperty("left").GetProperty("lineCount").GetInt32());
        Assert.False(root.GetProperty("identical").GetBoolean());
        Assert.Equal(2, root.GetProperty("differenceCount").GetInt32());
        Assert.Equal(1, root.GetProperty("lines").GetProperty("changed").GetInt32());
        Assert.Equal(1, root.GetProperty("lines").GetProperty("added").GetInt32());
        Assert.Equal(1, root.GetProperty("lines").GetProperty("removed").GetInt32());

        var hunks = root.GetProperty("hunks").EnumerateArray().ToArray();
        Assert.Equal(2, hunks.Length);
        Assert.Equal("changed", hunks[0].GetProperty("kind").GetString());
        Assert.False(hunks[0].GetProperty("ignored").GetBoolean());
        Assert.Equal(2, hunks[0].GetProperty("leftLine").GetInt32());
        Assert.Equal(1, hunks[0].GetProperty("leftCount").GetInt32());
        Assert.Equal(2, hunks[0].GetProperty("rightLine").GetInt32());
        Assert.Equal(2, hunks[0].GetProperty("rightCount").GetInt32());
        Assert.Equal(["old"], hunks[0].GetProperty("left").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["new", "more"], hunks[0].GetProperty("right").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("removed", hunks[1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("moves").ValueKind);
        Assert.Equal(
            ["version", "kind", "left", "right", "identical", "differenceCount", "lines", "hunks", "moves"],
            root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Json_marks_ignored_hunks_and_streams_many_hunks()
    {
        using (var ignored = FileJson(Compare("a\n\nb\n", "a\nb\n", CompareOptions.Default with { IgnoreBlankLines = true })))
        {
            Assert.True(ignored.RootElement.GetProperty("identical").GetBoolean());
            Assert.True(ignored.RootElement.GetProperty("hunks")[0].GetProperty("ignored").GetBoolean());
        }

        var left = string.Concat(Enumerable.Range(0, 50_000).Select(i => "same " + i + "\nleft " + i + "\n"));
        var right = string.Concat(Enumerable.Range(0, 50_000).Select(i => "same " + i + "\nright " + i + "\n"));
        using var many = FileJson(Compare(left, right));
        Assert.Equal(50_000, many.RootElement.GetProperty("differenceCount").GetInt32());
        Assert.Equal(50_000, many.RootElement.GetProperty("hunks").GetArrayLength());
    }

    [Fact]
    public void Json_for_a_folder_has_the_documented_shape()
    {
        var meta = new FileMeta(12, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 0, true);
        var missing = new FileMeta(0, DateTime.MinValue, 0, false);
        var items = new[]
        {
            new FolderCompareItem("sub\\a.txt", FolderItemKind.File, FolderCompareStatus.Different, meta, meta, 3, null, []),
            new FolderCompareItem("b.txt", FolderItemKind.File, FolderCompareStatus.RightOnly, missing, meta, null, null, []),
            new FolderCompareItem("c", FolderItemKind.Folder, FolderCompareStatus.Error, meta, meta, null, "denied", []),
        };
        var root = new FolderCompareItem(string.Empty, FolderItemKind.Folder, FolderCompareStatus.Different, meta, meta, null, null, items);
        var counts = new Dictionary<FolderCompareStatus, int> { [FolderCompareStatus.Different] = 1, [FolderCompareStatus.RightOnly] = 1, [FolderCompareStatus.Error] = 1 };
        var result = new FolderCompareResult(root, items, counts) { LeftRoot = "C:\\left", RightRoot = "C:\\right" };

        using var stream = new MemoryStream();
        JsonReportWriter.WriteFolder(result, stream);
        using var json = JsonDocument.Parse(stream.ToArray());
        var element = json.RootElement;

        Assert.Equal(1, element.GetProperty("version").GetInt32());
        Assert.Equal("folder", element.GetProperty("kind").GetString());
        Assert.Equal("C:/left", element.GetProperty("leftRoot").GetString());
        Assert.Equal("different", element.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("error").ValueKind);
        Assert.Equal(
            ["identical", "different", "leftOnly", "rightOnly", "typeConflict", "skipped", "error", "renamed", "moved"],
            element.GetProperty("counts").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, element.GetProperty("counts").GetProperty("rightOnly").GetInt32());
        Assert.Equal(0, element.GetProperty("counts").GetProperty("identical").GetInt32());

        var rows = element.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("sub/a.txt", rows[0].GetProperty("path").GetString());
        Assert.Equal("file", rows[0].GetProperty("type").GetString());
        Assert.Equal(3, rows[0].GetProperty("differenceCount").GetInt32());
        Assert.Equal(12, rows[0].GetProperty("left").GetProperty("size").GetInt64());
        Assert.Equal("2026-01-02T03:04:05.0000000Z", rows[0].GetProperty("left").GetProperty("modifiedUtc").GetString());
        Assert.Equal("rightOnly", rows[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("left").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("differenceCount").ValueKind);
        Assert.Equal("folder", rows[2].GetProperty("type").GetString());
        Assert.Equal("denied", rows[2].GetProperty("error").GetString());
        Assert.Equal(
            ["path", "type", "status", "differenceCount", "error", "left", "right"],
            rows[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Folder_report_embeds_a_diff_for_each_changed_file()
    {
        var left = Path.Combine(_root, "left");
        var right = Path.Combine(_root, "right");
        Directory.CreateDirectory(Path.Combine(left, "sub"));
        Directory.CreateDirectory(Path.Combine(right, "sub"));
        File.WriteAllText(Path.Combine(left, "sub", "code&1.cs"), "class A\n{\n    int x = 1;\n}\n");
        File.WriteAllText(Path.Combine(right, "sub", "code&1.cs"), "class A\n{\n    int x = 2;\n}\n");
        File.WriteAllBytes(Path.Combine(left, "data.bin"), [0, 1, 2, 0, 5]);
        File.WriteAllBytes(Path.Combine(right, "data.bin"), [0, 1, 3, 0, 5]);
        File.WriteAllText(Path.Combine(left, "same.txt"), "same");
        File.WriteAllText(Path.Combine(right, "same.txt"), "same");
        File.WriteAllText(Path.Combine(left, "only.txt"), "left only");
        var result = FolderComparer.Compare(left, right);

        using var writer = new StringWriter();
        FolderDiffReportWriter.WriteHtml(result, new FolderReportOptions { Title = "Folders" }, new FolderDiffReportOptions(), writer);
        var html = writer.ToString();

        Assert.Contains("<th>Path</th>", html);
        Assert.Contains(".sbs{", html);
        Assert.Contains("<h2>Changed files</h2>", html);
        Assert.Contains("<a href=\"#diff-1\">data.bin</a>", html);
        Assert.Contains("<section id=\"diff-2\"><h3>sub/code&amp;1.cs</h3>", html);
        Assert.Contains("Binary files differ.", html);
        Assert.Contains("<mark>1</mark>", html);
        Assert.Contains("<mark>2</mark>", html);
        Assert.DoesNotContain("<h3>same.txt", html);
        Assert.DoesNotContain("<h3>only.txt", html);
        Assert.Equal(1, CountOf(html, "</html>"));
        Assert.EndsWith("</body></html>", html);
    }

    [Fact]
    public void Cli_writes_diff_reports_in_each_format()
    {
        var left = Path.Combine(_root, "left.txt");
        var right = Path.Combine(_root, "right.txt");
        File.WriteAllText(left, "one\ntwo\n");
        File.WriteAllText(right, "one\n2\n");
        var html = Path.Combine(_root, "report.html");
        var markdown = Path.Combine(_root, "report.md");
        var json = Path.Combine(_root, "report.json");

        Assert.Equal(1, Program.Main(["diff", left, right, "--report", "html", "-o", html]));
        Assert.Equal(1, Program.Main(["diff", left, right, "--report", "md", "-o", markdown, "--left-label", "old"]));
        Assert.Equal(1, Program.Main(["diff", left, right, "--report=json", "--output", json]));
        Assert.Equal(2, Program.Main(["diff", left, right, "--report", "pdf", "-o", json]));

        Assert.Contains("<table class=\"sbs\">", File.ReadAllText(html));
        Assert.Contains("```diff\n--- old\n", File.ReadAllText(markdown));
        using var document = JsonDocument.Parse(File.ReadAllBytes(json));
        Assert.Equal("file", document.RootElement.GetProperty("kind").GetString());
        Assert.False(File.ReadAllBytes(html).Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public void Cli_folder_report_includes_diffs_on_request()
    {
        var left = Path.Combine(_root, "l");
        var right = Path.Combine(_root, "r");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "a.txt"), "old\n");
        File.WriteAllText(Path.Combine(right, "a.txt"), "new\n");
        var plain = Path.Combine(_root, "plain.html");
        var withDiffs = Path.Combine(_root, "diffs.html");

        Assert.Equal(1, Program.Main(["dir", left, right, "--report", plain]));
        Assert.Equal(1, Program.Main(["dir", left, right, "--report", withDiffs, "--include-diffs"]));

        Assert.DoesNotContain("Changed files", File.ReadAllText(plain));
        Assert.Contains("<mark>new</mark>", File.ReadAllText(withDiffs));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
