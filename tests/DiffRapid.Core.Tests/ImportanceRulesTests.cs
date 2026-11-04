using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;
using static DiffRapid.Core.Tests.TextComparerTests;

namespace DiffRapid.Core.Tests;

public class ImportanceRulesTests
{
    private static HunkImportance Single(TextDocument left, TextDocument right, ImportanceRules rules, CompareOptions? options = null)
    {
        var result = ImportanceClassifier.Classify(TextComparer.Compare(left, right, options), rules);
        return Assert.Single(result.Hunks);
    }

    [Fact]
    public void No_rules_keep_every_hunk_important()
    {
        var hunk = Single(Doc("int x = 1;"), Doc("int  x = 1;"), ImportanceRules.None);
        Assert.Equal(Importance.Important, hunk.Importance);
        Assert.Equal(UnimportantReasons.None, hunk.Reasons);
        Assert.Null(hunk.Reason);
    }

    [Fact]
    public void Whitespace_only_change_is_unimportant()
    {
        var hunk = Single(Doc("int x = 1;"), Doc("int  x =1;"), new ImportanceRules { WhitespaceIsUnimportant = true });
        Assert.Equal(Importance.Unimportant, hunk.Importance);
        Assert.Equal(UnimportantReasons.Whitespace, hunk.Reasons);
        Assert.Equal("whitespace", hunk.Reason);
    }

    [Fact]
    public void Rewrapped_lines_and_line_endings_count_as_whitespace()
    {
        var rules = new ImportanceRules { WhitespaceIsUnimportant = true };
        Assert.False(Single(Doc("one two", "three"), Doc("one", "two three"), rules).IsImportant);
        Assert.False(Single(TextDocument.Parse("a\r\n"), TextDocument.Parse("a\n"), rules).IsImportant);
        Assert.True(Single(TextDocument.Parse("a\r\n"), TextDocument.Parse("a\n"), ImportanceRules.None with { CaseIsUnimportant = true }).IsImportant);
    }

    [Fact]
    public void Case_only_change_is_unimportant()
    {
        var hunk = Single(Doc("Hello World"), Doc("hello world"), new ImportanceRules { CaseIsUnimportant = true });
        Assert.Equal(UnimportantReasons.Case, hunk.Reasons);
        Assert.True(Single(Doc("Hello World"), Doc("hello there"), new ImportanceRules { CaseIsUnimportant = true }).IsImportant);
    }

    [Fact]
    public void Blank_lines_only_change_is_unimportant()
    {
        var hunk = Single(Doc("a", "b"), Doc("a", "", "  ", "b"), new ImportanceRules { BlankLinesAreUnimportant = true });
        Assert.Equal(UnimportantReasons.BlankLines, hunk.Reasons);
        Assert.True(Single(Doc("a", "b"), Doc("a", "x", "b"), new ImportanceRules { BlankLinesAreUnimportant = true }).IsImportant);
    }

    [Fact]
    public void Hunk_ignored_by_compare_options_is_filtered()
    {
        var hunk = Single(Doc("a", "b"), Doc("a", "", "b"), ImportanceRules.None, CompareOptions.Default with { IgnoreBlankLines = true });
        Assert.Equal(UnimportantReasons.Filtered, hunk.Reasons);
    }

    [Theory]
    [InlineData("x = 1; // old", "x = 1; // new", true)]
    [InlineData("x = 1; // old", "x = 1;", true)]
    [InlineData("x = 1; /* a */ y();", "x = 1; /* b */ y();", true)]
    [InlineData("s = \"// a\";", "s = \"// b\";", false)]
    [InlineData("x = 1; // same", "x = 2; // same", false)]
    public void Comment_only_change_is_unimportant(string left, string right, bool unimportant)
    {
        var hunk = Single(Doc("start", left, "end"), Doc("start", right, "end"), new ImportanceRules { Comments = CommentSyntax.CStyle });
        Assert.Equal(unimportant, !hunk.IsImportant);
        if (unimportant)
        {
            Assert.Equal(UnimportantReasons.Comment, hunk.Reasons);
        }
    }

    [Fact]
    public void Block_comments_spanning_lines_are_tracked()
    {
        var rules = new ImportanceRules { Comments = CommentSyntax.CStyle };
        var hunk = Single(Doc("code();", "/* one", "two", "*/", "more();"), Doc("code();", "/* one", "three", "*/", "more();"), rules);
        Assert.Equal(UnimportantReasons.Comment, hunk.Reasons);
        var removed = Single(Doc("a();", "# note", "b();"), Doc("a();", "b();"), new ImportanceRules { Comments = CommentSyntax.Hash });
        Assert.Equal(UnimportantReasons.Comment, removed.Reasons);
        var xml = Single(Doc("<a/>", "<!-- don't -->", "<b/>"), Doc("<a/>", "<!-- do -->", "<b/>"), new ImportanceRules { Comments = CommentSyntax.Xml });
        Assert.Equal(UnimportantReasons.Comment, xml.Reasons);
    }

    [Fact]
    public void Unimportant_text_patterns_are_removed_before_comparing()
    {
        var rules = new ImportanceRules { UnimportantPatterns = new[] { @"\$Revision: \d+ \$", @"^// generated .*$" } };
        Assert.Equal(UnimportantReasons.Pattern, Single(Doc("v $Revision: 12 $"), Doc("v $Revision: 13 $"), rules).Reasons);
        Assert.Equal(UnimportantReasons.Pattern, Single(Doc("a", "// generated today", "b"), Doc("a", "b"), rules).Reasons);
        Assert.True(Single(Doc("v $Revision: 12 $ x"), Doc("v $Revision: 13 $ y"), rules).IsImportant);
    }

    [Fact]
    public void Invalid_pattern_is_reported()
    {
        var diff = TextComparer.Compare(Doc("a"), Doc("b"));
        Assert.Throws<ArgumentException>(() => ImportanceClassifier.Classify(diff, new ImportanceRules { UnimportantPatterns = new[] { "(" } }));
    }

    [Fact]
    public void Combined_rules_report_only_the_rules_needed()
    {
        var rules = new ImportanceRules { WhitespaceIsUnimportant = true, CaseIsUnimportant = true, Comments = CommentSyntax.CStyle };
        var hunk = Single(Doc("Foo(1); // a"), Doc("foo( 1 ); // b"), rules);
        Assert.Equal(UnimportantReasons.Whitespace | UnimportantReasons.Case | UnimportantReasons.Comment, hunk.Reasons);
        Assert.Equal("whitespace, case, comments", hunk.Reason);
        var caseAndSpace = Single(Doc("Foo(1);"), Doc("foo( 1 );"), rules);
        Assert.Equal(UnimportantReasons.Whitespace | UnimportantReasons.Case, caseAndSpace.Reasons);
    }

    [Fact]
    public void Compare_options_stay_in_force()
    {
        var options = CompareOptions.Default with { IgnoreCase = true };
        var hunk = Single(Doc("A = 1; // x"), Doc("a = 2; // x"), new ImportanceRules { Comments = CommentSyntax.CStyle }, options);
        Assert.True(hunk.IsImportant);
        var eol = Single(TextDocument.Parse("A // x\r\n"), TextDocument.Parse("a // y\n"),
            new ImportanceRules { Comments = CommentSyntax.CStyle }, options with { IgnoreLineEndings = true });
        Assert.Equal(UnimportantReasons.Comment, eol.Reasons);
    }

    [Fact]
    public void Custom_rule_explains_remaining_hunks()
    {
        var rule = new VarRule();
        var rules = new ImportanceRules { CustomRules = new IImportanceRule[] { rule } };
        var hunk = Single(Doc("a();", "int x = 1;", "b();"), Doc("a();", "var x = 1;", "b();"), rules);
        Assert.Equal(UnimportantReasons.Custom, hunk.Reasons);
        Assert.Equal("implicit type", hunk.Reason);
        Assert.True(Single(Doc("x"), Doc("y"), rules).IsImportant);
        Assert.False(Single(Doc("x "), Doc("x"), rules with { WhitespaceIsUnimportant = true }).IsImportant);
        Assert.Equal(2, rule.Calls);
    }

    [Fact]
    public void Inline_segments_are_flagged()
    {
        var rules = new ImportanceRules { WhitespaceIsUnimportant = true, Comments = CommentSyntax.CStyle };
        var hunk = Single(Doc("call(a,  b) + x; // first"), Doc("call(a, b) + y; // second"), rules);
        Assert.True(hunk.IsImportant);
        var line = Assert.Single(hunk.Lines);
        Assert.Equal((0, 0), (line.LeftLine, line.RightLine));
        var left = "call(a,  b) + x; // first";
        Assert.Contains(line.Left, s => left.Substring(s.Span.Start, s.Span.Length) == "  " && s.Reasons == UnimportantReasons.Whitespace);
        Assert.Contains(line.Left, s => left.Substring(s.Span.Start, s.Span.Length) == "x" && s.IsImportant);
        Assert.Contains(line.Left, s => left.Substring(s.Span.Start, s.Span.Length) == "first" && s.Reasons == UnimportantReasons.Comment);
    }

    [Fact]
    public void Case_segments_are_flagged()
    {
        var hunk = Single(Doc("Value x"), Doc("value y"), new ImportanceRules { CaseIsUnimportant = true });
        var line = Assert.Single(hunk.Lines);
        Assert.Contains(line.Right, s => s.Span == new TextSpan(0, 5) && s.Reasons == UnimportantReasons.Case);
        Assert.Contains(line.Right, s => s.Span == new TextSpan(6, 1) && s.IsImportant);
    }

    [Fact]
    public void Pattern_segments_are_flagged_and_unpaired_lines_have_no_segments()
    {
        var rules = new ImportanceRules { UnimportantPatterns = new[] { @"\d{4}-\d{2}-\d{2}" } };
        var result = ImportanceClassifier.Classify(TextComparer.Compare(Doc("built 2024-01-01 ok"), Doc("built 2025-02-02 ok", "extra")), rules);
        var hunk = Assert.Single(result.Hunks);
        var line = Assert.Single(hunk.Lines);
        Assert.All(line.Left, s => Assert.Equal(UnimportantReasons.Pattern, s.Reasons));
        Assert.True(hunk.IsImportant);
    }

    [Fact]
    public void Navigation_skips_unimportant_hunks()
    {
        var left = Doc("a", "B", "c", "d", "e", "f", "g");
        var right = Doc("a", "b", "c", "D2", "e", "F", "g");
        var result = ImportanceClassifier.Classify(TextComparer.Compare(left, right), new ImportanceRules { CaseIsUnimportant = true });
        Assert.Equal(3, result.Hunks.Count);
        Assert.Equal(1, result.ImportantCount);
        Assert.Equal(2, result.UnimportantCount);
        Assert.Equal(1, result.NextImportant(-1));
        Assert.Equal(-1, result.NextImportant(1));
        Assert.Equal(1, result.PreviousImportant(3));
        Assert.Equal(-1, result.PreviousImportant(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.NextImportant(-2));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.PreviousImportant(4));
    }

    [Fact]
    public void Classify_honours_cancellation()
    {
        var diff = TextComparer.Compare(Doc("a"), Doc("b"));
        Assert.Throws<OperationCanceledException>(() =>
            ImportanceClassifier.Classify(diff, new ImportanceRules { Comments = CommentSyntax.CStyle }, new CancellationToken(true)));
    }

    // Stands in for a semantic rule: "int x" and "var x" declare the same thing.
    private sealed class VarRule : IImportanceRule
    {
        public int Calls { get; private set; }

        public string? ExplainUnimportant(ImportanceContext context)
        {
            Calls++;
            var left = Enumerable.Range(context.Hunk.LeftStart, context.Hunk.LeftCount).Select(i => context.Left[i].Content.Replace("var ", "int "));
            var right = Enumerable.Range(context.Hunk.RightStart, context.Hunk.RightCount).Select(i => context.Right[i].Content.Replace("var ", "int "));
            return left.SequenceEqual(right) ? "implicit type" : null;
        }
    }
}
