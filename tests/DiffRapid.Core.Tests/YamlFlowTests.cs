using DiffRapid.Core.Structured;

namespace DiffRapid.Core.Tests;

/// <summary>Quoted scalars in long one-line flow collections are read in place.</summary>
public sealed class YamlFlowTests
{
    [Fact]
    public void LongFlowLinesOfQuotedScalarsAreReadWithoutCopyingTheRestOfTheLine()
    {
        const int count = 20_000;
        var text = "items: [" + string.Join(", ", Enumerable.Range(0, count).Select(index => $"\"v{index}\"")) + "]\n";
        var before = GC.GetAllocatedBytesForCurrentThread();

        var document = YamlTreeReader.Read(text, cancellationToken: TestContext.Current.CancellationToken);

        // Copying the rest of the line for each item would allocate about 2.6 GB here.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256L * 1024 * 1024);
        var items = Assert.Single(document.Children).Children;
        Assert.Equal(count, items.Count);
        Assert.Equal("v19999", items[^1].Value);
    }

    [Fact]
    public void QuotedFlowErrorsKeepTheirColumns()
    {
        var unclosed = Assert.Throws<StructuredFormatException>(() => YamlTreeReader.Read("a: [1, \"open]\n"));
        Assert.Equal(8, unclosed.Column);
        var escape = Assert.Throws<StructuredFormatException>(() => YamlTreeReader.Read("a: [\"x\\q\"]\n"));
        Assert.Equal(7, escape.Column);
        Assert.Equal("it's", Assert.Single(Assert.Single(YamlTreeReader.Read("a: ['it''s']\n").Children).Children).Value);
    }
}
