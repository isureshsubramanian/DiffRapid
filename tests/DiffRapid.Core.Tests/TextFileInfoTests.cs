using System.Text;
using DiffRapid.Cli;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Tests;

public sealed class TextFileInfoTests
{
    [Theory]
    [InlineData(65001, false, "UTF-8")]
    [InlineData(65001, true, "UTF-8 with BOM")]
    [InlineData(1200, true, "UTF-16 LE")]
    [InlineData(1201, true, "UTF-16 BE")]
    [InlineData(12000, true, "UTF-32 LE")]
    [InlineData(12001, true, "UTF-32 BE")]
    public void DetectsEncoding(int codePage, bool bom, string name)
    {
        var encoding = Encoding.GetEncoding(codePage);
        byte[] bytes = [.. bom ? encoding.GetPreamble() : [], .. encoding.GetBytes("hello\n")];
        var result = TextFileReader.Decode(bytes);
        Assert.Equal("hello\n", result.Text);
        Assert.Equal(name, result.Info.EncodingName);
        Assert.Equal(bom, result.Info.HasBom);
        Assert.False(result.IsBinary);
    }

    [Fact]
    public void InvalidUtf8FallsBackToLatin1()
    {
        var result = TextFileReader.Decode([0xE9]);
        Assert.Equal("\u00e9", result.Text);
        Assert.Equal("Latin-1", result.Info.EncodingName);
        Assert.False(result.Info.HasBom);
        Assert.Equal(1201, TextFileReader.Decode([0xFF], Encoding.BigEndianUnicode).Encoding.CodePage);
    }

    [Theory]
    [InlineData("", LineEnding.None, false, false, 0)]
    [InlineData("hello", LineEnding.None, false, false, 1)]
    [InlineData("a\nb", LineEnding.Lf, false, false, 2)]
    [InlineData("a\r\nb\nc\r\n", LineEnding.CrLf, true, true, 3)]
    [InlineData("a\r\nb\n", LineEnding.CrLf, true, true, 2)]
    [InlineData("a\nb\nc\r", LineEnding.Lf, true, true, 3)]
    [InlineData("\r\r", LineEnding.Cr, false, true, 2)]
    public void ReportsLineEndings(string text, LineEnding dominant, bool mixed, bool trailing, int count)
    {
        var info = TextFileReader.Decode(Encoding.UTF8.GetBytes(text)).Info;
        Assert.Equal(dominant, info.DominantLineEnding);
        Assert.Equal(mixed, info.IsMixedLineEndings);
        Assert.Equal(trailing, info.HasTrailingLineEnding);
        Assert.Equal(count, info.LineCount);
        Assert.Equal(TextDocument.Parse(text).LineCount, count);
    }

    [Fact]
    public void OverrideBypassesDetectionAndHandlesUnicode()
    {
        Assert.Equal("\u00c3\u00a9", TextFileReader.Decode([0xC3, 0xA9],
            overrideEncoding: Encoding.GetEncoding(28591)).Text);
        var result = TextFileReader.Decode([0xFF, 0xFE, 0x61, 0], overrideEncoding: Encoding.Unicode);
        Assert.Equal("a", result.Text);
        Assert.True(result.Info.HasBom);
        Assert.False(result.IsBinary);
        Assert.False(TextFileReader.Decode([0x61, 0], overrideEncoding: Encoding.Unicode).IsBinary);
        Assert.False(TextFileReader.Decode([0xEF, 0xBB, 0xBF],
            overrideEncoding: Encoding.GetEncoding(28591)).Info.HasBom);
    }

    [Fact]
    public void CliInfoAndCodePageOverride()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.txt");
            var right = Path.Combine(directory, "right.txt");
            File.WriteAllBytes(left, [0x80]);
            File.WriteAllBytes(right, [0x3F]);
            Assert.Equal(1, Program.Main(["diff", left, right, "--quiet"]));
            Assert.Equal(0, Program.Main(["diff", left, right, "--quiet", "--cp", "20127"]));
            Assert.Equal(1, Program.Main(["diff", left, right, "--quiet", "--cp", "1252"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--cp", "invalid"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--cp", "99999"]));
            Assert.Equal(2, Program.Main(["diff", left, right, "--cp"]));
            Assert.Equal(0, Program.Main(["info", left]));
            Assert.Equal(2, Program.Main(["info"]));
            Assert.Equal(2, Program.Main(["info", left, right]));
            Assert.Equal("\u20ac", TextFileReader.ReadFile(left,
                overrideEncoding: Encoding.GetEncoding(1252)).Text);
            Assert.Equal("Encoding: Latin-1\nHas BOM: False\nDominant line ending: NONE\n" +
                "Mixed line endings: False\nTrailing line ending: False\nLine count: 1\n",
                Program.FormatInfo(TextFileReader.ReadFile(left).Info));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
