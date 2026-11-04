using DiffRapid.Core.Folders;

namespace DiffRapid.Core.Tests;

public sealed class FolderFilterTests
{
    [Fact]
    public void Parses_all_keywords_and_comments()
    {
        var filter = FolderFilter.Parse("""

              ## comment
            NAME: Build output ## trailing
            DeSc: Generated files
            DEF: INCLUDE
            F: \.tmp$ ## comment
            XF: ^keep
            D: \\bin$
            XD: ^\\src\\bin$
            """);
        Assert.Equal("Build output", filter.Name);
        Assert.Equal("Generated files", filter.Description);
        Assert.True(filter.DefaultInclude);
        Assert.False(filter.IsFileIncluded("FILE.TMP"));
        Assert.True(filter.IsFileIncluded("KEEP.tmp"));
        Assert.True(filter.IsFileIncluded("file.txt"));
        Assert.False(filter.IsFolderIncluded("BIN"));
        Assert.True(filter.IsFolderIncluded("SRC/bin"));
        Assert.False(filter.IsFolderIncluded("src/deep/bin"));
    }

    [Theory]
    [InlineData("include", false, true, true)]
    [InlineData("exclude", true, false, false)]
    public void Modes_apply_rules_and_exceptions(string mode, bool match, bool exception, bool unmatched)
    {
        var filter = FolderFilter.Parse($"def: {mode}\nf: \\.cs$\nxf: ^test\nd: \\\\src$\nxd: ^\\\\test");
        Assert.Equal(match, filter.IsFileIncluded("main.cs"));
        Assert.Equal(exception, filter.IsFileIncluded("test.cs"));
        Assert.Equal(unmatched, filter.IsFileIncluded("readme.md"));
        Assert.Equal(match, filter.IsFolderIncluded("src"));
        Assert.Equal(exception, filter.IsFolderIncluded("test/src"));
        Assert.Equal(unmatched, filter.IsFolderIncluded("docs"));
    }

    [Theory]
    [InlineData("src", false)]
    [InlineData("src/bin", false)]
    [InlineData("src/deep/bin", false)]
    [InlineData(@"\src\deep\bin", false)]
    [InlineData("other/src", true)]
    public void Folder_paths_are_normalized(string path, bool included)
    {
        Assert.Equal(included, FolderFilter.Parse(@"d: ^\\src(?:\\|$)").IsFolderIncluded(path));
    }

    [Fact]
    public void Defaults_and_literal_hashes_are_preserved()
    {
        var filter = FolderFilter.Parse("");
        Assert.Equal("", filter.Name);
        Assert.Equal("", filter.Description);
        Assert.True(filter.DefaultInclude);
        Assert.True(filter.IsFileIncluded("a"));
        Assert.True(filter.IsFolderIncluded("a"));
        // A kind of entry with no rules is not filtered, whatever the default.
        Assert.True(FolderFilter.Parse("def: exclude").IsFileIncluded("a"));
        Assert.True(FolderFilter.Parse("def: exclude").IsFolderIncluded("a"));
        Assert.False(FolderFilter.Parse("f: ^a##b$ ## comment").IsFileIncluded("a##b"));
    }

    [Theory]
    [InlineData("f: [")]
    [InlineData("d: [")]
    [InlineData("xf: [")]
    [InlineData("xd: [")]
    [InlineData("unknown: value")]
    [InlineData("missing colon")]
    [InlineData("def: invalid")]
    public void Errors_report_physical_lines(string rule)
    {
        Assert.Contains("line 3", Assert.Throws<FormatException>(() => FolderFilter.Parse("## comment\n\n" + rule)).Message);
    }

    [Fact]
    public void File_only_filter_keeps_every_folder()
    {
        var filter = FolderFilter.Parse("def: exclude\nf: \\.cs$");
        Assert.True(filter.IsFileIncluded("Program.cs"));
        Assert.False(filter.IsFileIncluded("readme.md"));
        Assert.True(filter.IsFolderIncluded("src/App"));
    }
}
