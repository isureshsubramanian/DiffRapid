using DiffRapid.Core.Merge;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public sealed class ConflictFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripsMerge(bool includeBase)
    {
        const string original = "common\nold\nend\n";
        const string left = "common\nours\nend\n";
        const string right = "common\ntheirs\nend\n";
        var merge = ThreeWayMerger.Merge(TextDocument.Parse(original), TextDocument.Parse(left), TextDocument.Parse(right));
        var parts = ConflictFile.Parse(merge.ToText(new ConflictLabels(" ours ", " ancestor ", " theirs "), includeBase));
        Assert.Equal(left, parts.Left);
        Assert.Equal(right, parts.Right);
        Assert.Equal(includeBase ? original : null, parts.Base);
        Assert.Equal(1, parts.ConflictCount);
        Assert.Equal("ours", parts.LeftLabel);
        Assert.Equal(includeBase ? "ancestor" : null, parts.BaseLabel);
        Assert.Equal("theirs", parts.RightLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain\r\ntext\nlast")]
    [InlineData("text =======\n========\n<<<<<<<x\n")]
    public void NoMarkersCopiesText(string text)
    {
        var parts = ConflictFile.Parse(text);
        Assert.Equal(text, parts.Left);
        Assert.Equal(text, parts.Right);
        Assert.Equal(text, parts.Base);
        Assert.Equal(0, parts.ConflictCount);
        Assert.Null(parts.LeftLabel);
        Assert.Null(parts.RightLabel);
        Assert.Null(parts.BaseLabel);
    }

    [Fact]
    public void PreservesMixedEndingsAndFinalUnterminatedLine()
    {
        var parts = ConflictFile.Parse("head\r\n<<<<<<<\r\nleft\r|||||||\nbase\r\n=======\r\nright\n>>>>>>>\r\ntail");
        Assert.Equal("head\r\nleft\rtail", parts.Left);
        Assert.Equal("head\r\nright\ntail", parts.Right);
        Assert.Equal("head\r\nbase\r\ntail", parts.Base);
        Assert.Null(parts.LeftLabel);
    }

    [Fact]
    public void EmptySectionsAndClosingMarkerWithoutEnding()
    {
        var parts = ConflictFile.Parse("<<<<<<<\n|||||||\n=======\n>>>>>>>");
        Assert.Equal("", parts.Left);
        Assert.Equal("", parts.Right);
        Assert.Equal("", parts.Base);
    }

    [Fact]
    public void MixedBaseAvailabilityAndFirstLabels()
    {
        var parts = ConflictFile.Parse("<<<<<<<\tfirst\n=======\n>>>>>>> first-right\n<<<<<<< second\n||||||| first-base\n=======\n>>>>>>> second-right\n");
        Assert.Equal(2, parts.ConflictCount);
        Assert.Null(parts.Base);
        Assert.Equal("first", parts.LeftLabel);
        Assert.Equal("first-base", parts.BaseLabel);
        Assert.Equal("first-right", parts.RightLabel);
    }

    [Theory]
    [InlineData("ok\n=======\n", 2)]
    [InlineData("ok\n<<<<<<<\nleft", 2)]
    [InlineData("<<<<<<<\n<<<<<<<\n", 2)]
    [InlineData("<<<<<<<\n=======\n|||||||\n", 3)]
    [InlineData("|||||||\n", 1)]
    [InlineData(">>>>>>>\n", 1)]
    [InlineData("<<<<<<<\n>>>>>>>\n", 2)]
    [InlineData("<<<<<<<\n|||||||\n|||||||\n", 3)]
    [InlineData("<<<<<<<\n=======\n=======\n", 3)]
    public void RejectsMalformedStructure(string text, int line)
    {
        Assert.Contains($"line {line}", Assert.Throws<FormatException>(() => ConflictFile.Parse(text)).Message);
    }

    [Fact]
    public void UnterminatedContentInsideConflictIsRejected()
    {
        Assert.Contains("line 1", Assert.Throws<FormatException>(() => ConflictFile.Parse("<<<<<<<\nlast without ending")).Message);
    }
}
