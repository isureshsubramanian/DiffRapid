using DiffRapid.Cli;
using DiffRapid.Core.Comparison;

namespace DiffRapid.Core.Tests;

/// <summary>Verifies option parsing used by revision and file commands.</summary>
public sealed class RevisionCommandTests
{
    /// <summary>Verifies revision values and history count aliases.</summary>
    [Theory]
    [InlineData("-n", "3")]
    [InlineData("--count=3", null)]
    [InlineData("/n:3", null)]
    public void ParsesHistoryCountAliases(string option, string? value)
    {
        string[] arguments = value is null ? ["log", "file", option] : ["log", "file", option, value];
        var parsed = Program.ParsedArgs.Parse(arguments, 1);
        Assert.Equal(["file"], parsed.Positionals);
        Assert.Equal(3, parsed.IntValue("count", 20));
    }

    /// <summary>Verifies legacy whitespace tokens and repeated filter values.</summary>
    [Theory]
    [InlineData("/ignorews", WhitespaceMode.IgnoreChange)]
    [InlineData("/ignorews:1", WhitespaceMode.IgnoreChange)]
    [InlineData("/ignorews:2", WhitespaceMode.IgnoreAll)]
    public void ParsesWhitespaceAndRepeatedOptions(string option, WhitespaceMode expected)
    {
        var parsed = Program.ParsedArgs.Parse(["diff", "file", "--rev=HEAD~1", option, "--include=*.cs", "--include", "*.txt", "--exclude=bin"], 1);
        Assert.Equal("HEAD~1", parsed.Value("rev"));
        Assert.Equal(expected, parsed.ToCompareOptions().Whitespace);
        Assert.Equal(["*.cs", "*.txt"], parsed.Values("include"));
        Assert.Equal(["bin"], parsed.Values("exclude"));
    }
}
