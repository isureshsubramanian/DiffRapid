using DiffRapid.Core.Comparison;
using static DiffRapid.Core.Tests.TextComparerTests;

namespace DiffRapid.Core.Tests;

public sealed class FilterTests
{
    [Theory]
    [InlineData("// old", "// new", true)]
    [InlineData("// old\ncode", "// new\nchanged", false)]
    [InlineData("code", "// new", false)]
    [InlineData("", "// added", true)]
    [InlineData("// removed", "", true)]
    public void Line_filters_require_every_changed_line(string left, string right, bool equal)
    {
        var result = TextComparer.Compare(Doc(left), Doc(right), new CompareOptions
        {
            LineFilters = [new("^//"), new("^$")],
        });

        Assert.Equal(equal, result.AreEqual);
        Assert.Equal(equal ? DiffSignificance.Ignored : DiffSignificance.Important, Assert.Single(result.Hunks).Significance);
    }

    [Fact]
    public void Substitutions_preserve_displayed_text_and_run_before_normalization()
    {
        var left = Doc("version 1.2");
        var result = TextComparer.Compare(left, Doc("VERSION stable"), new CompareOptions
        {
            Substitutions = [new(@"\d+\.\d+", "  STABLE  ", true, true, false)],
            IgnoreCase = true,
            Whitespace = WhitespaceMode.IgnoreChange,
        });

        Assert.True(result.AreEqual);
        Assert.Equal("version 1.2", result.Left[0].Content);
    }

    [Fact]
    public void Disabled_filters_do_nothing_even_with_invalid_patterns()
    {
        Assert.False(TextComparer.Compare(Doc("old"), Doc("new"), new CompareOptions
        {
            LineFilters = [new("[", false)],
            Substitutions = [new("[", "new", true, true, false, false)],
        }).AreEqual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_patterns_identify_the_pattern(bool lineFilter)
    {
        var options = lineFilter
            ? new CompareOptions { LineFilters = [new("[")] }
            : new CompareOptions { Substitutions = [new("[", "", true, true, false)] };
        var error = Assert.Throws<ArgumentException>(() => TextComparer.Compare(Doc("same"), Doc("same"), options));
        Assert.Contains("'['", error.Message);
    }

    [Fact]
    public void Literal_whole_word_substitutions_respect_case_and_order()
    {
        var options = new CompareOptions
        {
            Substitutions = [new("v.1", "$1", false, false, true), new("$1", "done", false, true, false)],
        };
        Assert.True(TextComparer.Compare(Doc("V.1 av.1"), Doc("done av.1"), options).AreEqual);
        Assert.False(TextComparer.Compare(Doc("V.1"), Doc("done"), options with
        {
            Substitutions = [new("v.1", "done", false, true, true)],
        }).AreEqual);
    }

    [Fact]
    public void Equal_lines_are_unaffected_and_regex_capture_replacements_work()
    {
        var options = new CompareOptions
        {
            LineFilters = [new("^//")],
            Substitutions = [new(@"version (\d+)\.\d+", "version $1", true, true, false)],
        };
        var result = TextComparer.Compare(Doc("code", "version 2.1"), Doc("code", "version 2.9"), options);
        Assert.True(result.AreEqual);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void Options_snapshot_lists_and_compare_by_value()
    {
        var filters = new List<LineFilter> { new("comment") };
        var options = new CompareOptions { LineFilters = filters, Substitutions = [new("a", "b", true, true, false)] };
        var copy = options with { LineFilters = [new("comment")], Substitutions = [new("a", "b", true, true, false)] };
        filters.Clear();
        Assert.Equal(options, copy);
        Assert.Equal(options.GetHashCode(), copy.GetHashCode());
        Assert.Single(options.LineFilters);
        Assert.Equal(new CompareOptions(), new CompareOptions { LineFilters = [], Substitutions = [] });
        Assert.NotEqual(options, options with { Substitutions = [] });
    }
}
