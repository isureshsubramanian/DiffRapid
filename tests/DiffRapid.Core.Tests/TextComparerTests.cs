using DiffRapid.Core.Comparison;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public class TextComparerTests
{
    [Fact]
    public void Parse_preserves_line_endings_and_round_trips()
    {
        const string text = "a\r\nb\nc\rd";
        var doc = TextDocument.Parse(text);

        Assert.Equal(4, doc.LineCount);
        Assert.Equal([LineEnding.CrLf, LineEnding.Lf, LineEnding.Cr, LineEnding.None], doc.Lines.Select(l => l.Ending));
        Assert.True(doc.MissingFinalLineEnding);
        Assert.Equal(text, doc.ToString());
    }

    [Fact]
    public void Detects_changed_added_and_removed_blocks()
    {
        var left = Doc("one", "two", "three", "four");
        var right = Doc("one", "TWO", "three", "four", "five");

        var result = TextComparer.Compare(left, right);

        Assert.Equal(2, result.DifferenceCount);
        Assert.Equal(DiffKind.Changed, result.Hunks[0].Kind);
        Assert.Equal(DiffKind.Added, result.Hunks[1].Kind);
    }

    [Fact]
    public void Line_endings_differ_unless_ignored()
    {
        var left = TextDocument.Parse("a\r\nb\r\n");
        var right = TextDocument.Parse("a\nb\n");

        Assert.False(TextComparer.Compare(left, right).AreEqual);
        Assert.True(TextComparer.Compare(left, right, new CompareOptions { IgnoreLineEndings = true }).AreEqual);
    }

    [Fact]
    public void Missing_trailing_line_ending_can_be_ignored()
    {
        var left = TextDocument.Parse("a\nb\n");
        var right = TextDocument.Parse("a\nb");

        Assert.False(TextComparer.Compare(left, right).AreEqual);
        Assert.True(TextComparer.Compare(left, right, new CompareOptions { IgnoreMissingTrailingLineEnding = true }).AreEqual);
    }

    [Theory]
    [InlineData(WhitespaceMode.Compare, false, false)]
    [InlineData(WhitespaceMode.IgnoreChange, true, false)]
    [InlineData(WhitespaceMode.IgnoreAll, true, true)]
    public void Whitespace_modes(WhitespaceMode mode, bool amountIgnored, bool insertionIgnored)
    {
        var options = new CompareOptions { Whitespace = mode };

        Assert.Equal(amountIgnored, TextComparer.Compare(Doc("int  x = 1;  "), Doc("  int x = 1;"), options).AreEqual);
        Assert.Equal(insertionIgnored, TextComparer.Compare(Doc("a+b"), Doc("a + b"), options).AreEqual);
    }

    [Fact]
    public void Case_and_numbers_can_be_ignored()
    {
        Assert.True(TextComparer.Compare(Doc("Hello"), Doc("HELLO"), new CompareOptions { IgnoreCase = true }).AreEqual);
        Assert.True(TextComparer.Compare(Doc("build 1234"), Doc("build 98"), new CompareOptions { IgnoreNumbers = true }).AreEqual);
        Assert.False(TextComparer.Compare(Doc("build 1234"), Doc("build x"), new CompareOptions { IgnoreNumbers = true }).AreEqual);
    }

    [Fact]
    public void Blank_line_changes_are_classified_as_ignored()
    {
        var result = TextComparer.Compare(Doc("a", "b"), Doc("a", "", "b"), new CompareOptions { IgnoreBlankLines = true });

        Assert.True(result.AreEqual);
        Assert.Single(result.Hunks);
        Assert.Equal(DiffSignificance.Ignored, result.Hunks[0].Significance);
    }

    [Fact]
    public void Moved_blocks_are_detected_across_hunks()
    {
        var left = Doc("header", "void Moved() { DoWork(); }", "middle", "footer");
        var right = Doc("header", "middle", "void Moved() { DoWork(); }", "footer");

        var result = TextComparer.Compare(left, right);

        // Either line may be chosen as the moved one; both are valid minimal interpretations.
        var move = Assert.Single(result.MovedBlocks);
        Assert.Equal(1, move.LineCount);
        Assert.Equal(left[move.LeftStart].Content, right[move.RightStart].Content);
        Assert.NotEqual(move.LeftStart, move.RightStart);
    }

    [Fact]
    public void Trivial_lines_are_not_reported_as_moves()
    {
        var left = Doc("a1", "}", "b1");
        var right = Doc("b1", "}", "a2");

        var result = TextComparer.Compare(left, right);

        Assert.Empty(result.MovedBlocks);
    }

    [Fact]
    public void Inline_word_diff_highlights_changed_words()
    {
        var diff = InlineDiffer.Compare("var total = price * qty;", "var total = cost * qty;");

        var left = Assert.Single(diff.Left);
        var right = Assert.Single(diff.Right);
        Assert.Equal("price", "var total = price * qty;".Substring(left.Start, left.Length));
        Assert.Equal("cost", "var total = cost * qty;".Substring(right.Start, right.Length));
    }

    [Fact]
    public void Inline_character_diff()
    {
        var diff = InlineDiffer.Compare("color", "colour", new CompareOptions { InlineGranularity = InlineGranularity.Character });

        Assert.Empty(diff.Left);
        Assert.Equal([new TextSpan(4, 1)], diff.Right);
    }

    [Fact]
    public void Alignment_pads_the_shorter_side()
    {
        var result = TextComparer.Compare(Doc("a", "b", "c"), Doc("a", "x", "y", "c"));

        var rows = DiffAlignment.Build(result);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new AlignedRow(1, 1, AlignedRowKind.Changed, 0), rows[1]);
        Assert.Equal(new AlignedRow(null, 2, AlignedRowKind.Added, 0), rows[2]);
        Assert.Equal(new AlignedRow(2, 3, AlignedRowKind.Equal, -1), rows[3]);
    }

    [Fact]
    public void Unified_diff_matches_the_standard_format()
    {
        var left = TextDocument.Parse("1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n");
        var right = TextDocument.Parse("1\n2\n3\nfour\n5\n6\n7\n8\n9\n10\neleven");

        var patch = UnifiedDiffWriter.Write(TextComparer.Compare(left, right), "a/file.txt", "b/file.txt");

        const string expected =
            "--- a/file.txt\n" +
            "+++ b/file.txt\n" +
            "@@ -1,10 +1,11 @@\n" +
            " 1\n 2\n 3\n-4\n+four\n 5\n 6\n 7\n 8\n 9\n 10\n+eleven\n" +
            "\\ No newline at end of file\n";
        Assert.Equal(expected, patch);
    }

    [Fact]
    public void Unified_diff_splits_distant_hunks()
    {
        var leftLines = Enumerable.Range(1, 30).Select(i => i.ToString()).ToArray();
        var rightLines = leftLines.ToArray();
        rightLines[1] = "two";
        rightLines[25] = "twenty-six";

        var patch = UnifiedDiffWriter.Write(TextComparer.Compare(Doc(leftLines), Doc(rightLines)), "a", "b");

        Assert.Contains("@@ -1,5 +1,5 @@", patch);
        Assert.Contains("@@ -23,7 +23,7 @@", patch);
    }

    [Fact]
    public void Decoder_detects_bom_utf8_and_binary()
    {
        var bom = TextFileReader.Decode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);
        Assert.True(bom.HasByteOrderMark);
        Assert.Equal("hi", bom.Text);

        var utf8 = TextFileReader.Decode(System.Text.Encoding.UTF8.GetBytes("héllo"));
        Assert.Equal("héllo", utf8.Text);
        Assert.False(utf8.IsBinary);

        var latin = TextFileReader.Decode([(byte)'h', 0xE9]);
        Assert.Equal("hé", latin.Text);

        Assert.True(TextFileReader.Decode([1, 0, 2]).IsBinary);
    }

    internal static TextDocument Doc(params string[] lines) =>
        TextDocument.Parse(string.Concat(lines.Select(l => l + "\n")));
}
