using DiffRapid.Core.Binary;

namespace DiffRapid.Core.Tests;

public sealed class HexFormatterTests
{
    private static readonly byte[] Sample = [.. "Hello, hex!\0\x01\x7F~ ABCDEFGHIJKLMNOP".Select(c => (byte)c)];

    [Fact]
    public void DefaultLineHasOffsetHexAndAscii()
    {
        var formatter = new HexFormatter();
        var line = formatter.FormatLine(0x20, Sample, 0, 16);
        Assert.Equal("00000020", line.OffsetText);
        Assert.Equal("48 65 6C 6C 6F 2C 20 68 65 78 21 00 01 7F 7E 20", line.Hex);
        Assert.Equal("Hello, hex!...~ ", line.Ascii);
        Assert.Equal(47, formatter.HexWidth);
        Assert.Equal("00000020  48 65 6C 6C 6F 2C 20 68 65 78 21 00 01 7F 7E 20  Hello, hex!...~ ", formatter.FormatText(line));
        Assert.False(line.HasDifference);
    }

    [Theory]
    [InlineData(8, 1, 23)]
    [InlineData(16, 1, 47)]
    [InlineData(32, 1, 95)]
    [InlineData(16, 2, 39)]
    [InlineData(16, 4, 35)]
    [InlineData(32, 8, 67)]
    public void WidthsFollowBytesPerLineAndGroupSize(int bytesPerLine, int groupSize, int hexWidth)
    {
        var formatter = new HexFormatter(new HexFormatOptions { BytesPerLine = bytesPerLine, GroupSize = groupSize });
        Assert.Equal(hexWidth, formatter.HexWidth);
        var line = formatter.FormatLine(0, Sample, 0, 3);
        Assert.Equal(hexWidth, line.Hex.Length);
        Assert.Equal(3, line.Bytes.Count);
        Assert.Equal("Hel", line.Ascii);
    }

    [Fact]
    public void GroupsShowBytesInTheChosenOrder()
    {
        byte[] bytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var big = new HexFormatter(new HexFormatOptions { BytesPerLine = 8, GroupSize = 4 });
        Assert.Equal("01020304 05060708", big.FormatLine(0, bytes, 0, 8).Hex);
        var little = new HexFormatter(new HexFormatOptions { BytesPerLine = 8, GroupSize = 4, ByteOrder = HexByteOrder.LittleEndian });
        Assert.Equal("04030201 08070605", little.FormatLine(0, bytes, 0, 8).Hex);
        var little2 = new HexFormatter(new HexFormatOptions { BytesPerLine = 8, GroupSize = 2, ByteOrder = HexByteOrder.LittleEndian, UpperCase = false });
        Assert.Equal("0201 0403 0605 0807", little2.FormatLine(0, bytes, 0, 8).Hex);
        var little8 = new HexFormatter(new HexFormatOptions { BytesPerLine = 8, GroupSize = 8, ByteOrder = HexByteOrder.LittleEndian });
        Assert.Equal("0807060504030201", little8.FormatLine(0, bytes, 0, 8).Hex);

        // A partial group keeps each byte in its column.
        Assert.Equal("    0201         ", little.FormatLine(0, bytes, 0, 2).Hex);
        Assert.Equal(6, little.HexColumn(0));
        Assert.Equal(4, little.HexColumn(1));
        Assert.Equal(15, little.HexColumn(4));
        Assert.Equal(9, big.HexColumn(4));
    }

    [Fact]
    public void MarksBytesInsideRangesOnTheGivenSide()
    {
        BinaryDiffRange[] ranges =
        [
            new(2, 3, 2, 3, BinaryDiffKind.Changed),
            new(14, 0, 14, 4, BinaryDiffKind.Inserted),
            new(30, 1, 34, 1, BinaryDiffKind.Changed),
        ];
        var formatter = new HexFormatter();
        var left = formatter.FormatLine(0, Sample, 0, 16, ranges, BinarySide.Left);
        Assert.Equal(new[] { 2, 3, 4 }, Enumerable.Range(0, 16).Where(i => left.Different[i]));
        var right = formatter.FormatLine(0, Sample, 0, 16, ranges, BinarySide.Right);
        Assert.Equal(new[] { 2, 3, 4, 14, 15 }, Enumerable.Range(0, 16).Where(i => right.Different[i]));
        var rightNext = formatter.FormatLine(16, Sample, 16, 16, ranges, BinarySide.Right);
        Assert.Equal(new[] { 0, 1 }, Enumerable.Range(0, 16).Where(i => rightNext.Different[i]));
        var leftNext = formatter.FormatLine(16, Sample, 16, 16, ranges, BinarySide.Left);
        Assert.Equal(new[] { 14 }, Enumerable.Range(0, 16).Where(i => leftNext.Different[i]));
        Assert.True(leftNext.HasDifference);
    }

    [Fact]
    public void RejectsInvalidLayouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexFormatOptions { BytesPerLine = 12 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexFormatOptions { GroupSize = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexFormatOptions { OffsetDigits = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexFormatter().FormatLine(0, Sample, 0, 17));
        Assert.Equal("0000000000ab", new HexFormatter(new HexFormatOptions { OffsetDigits = 12, UpperCase = false }).FormatOffset(0xAB));
    }

    [Fact]
    public void AlignedLayoutFollowsTheLeftGridAndPadsTheShorterTail()
    {
        var left = BinaryComparerTests.Random(40, 21);
        var right = left.Take(20).ToArray();
        right[3] ^= 1;
        var result = BinaryComparer.Compare(left, right);
        var rows = HexPairedView.Layout(result, 16).ToList();
        Assert.Equal(
            [
                new HexRowLayout(0, 16, 0, 16, true),
                new HexRowLayout(16, 16, 16, 4, true),
                new HexRowLayout(32, 8, 20, 0, true),
            ],
            rows);
    }

    [Fact]
    public void ResyncLayoutAddsFillerSoLaterBytesLineUp()
    {
        var left = BinaryComparerTests.Random(64, 22);
        var right = left.Take(16).Concat(Enumerable.Repeat((byte)0xAA, 20)).Concat(left.Skip(16)).ToArray();
        var result = new BinaryDiffResultBuilder(left.Length, right.Length).Add(16, 0, 16, 20).Build();
        var rows = HexPairedView.Layout(result, 16).ToList();
        Assert.Equal(
            [
                new HexRowLayout(0, 16, 0, 16, false),
                new HexRowLayout(16, 0, 16, 16, true),
                new HexRowLayout(16, 0, 32, 4, true),
                new HexRowLayout(16, 16, 36, 16, false),
                new HexRowLayout(32, 16, 52, 16, false),
                new HexRowLayout(48, 16, 68, 16, false),
            ],
            rows);

        using var leftStream = new MemoryStream(left);
        using var rightStream = new MemoryStream(right);
        var pairs = HexPairedView.Build(leftStream, rightStream, result, new HexFormatter()).ToList();
        Assert.Null(pairs[1].Left);
        Assert.All(pairs[1].Right!.Different, Assert.True);
        Assert.Equal(pairs[3].Left!.Hex, pairs[3].Right!.Hex);
        Assert.Equal(0x24, pairs[3].Right!.Offset);
    }

    [Fact]
    public void ChangeOfDifferentLengthsUsesItsOwnRows()
    {
        var result = new BinaryDiffResultBuilder(40, 50).Add(8, 4, 8, 14).Build();
        var rows = HexPairedView.Layout(result, 8).ToList();
        Assert.Equal(
            [
                new HexRowLayout(0, 8, 0, 8, false),
                new HexRowLayout(8, 4, 8, 8, true),
                new HexRowLayout(12, 0, 16, 6, true),
                new HexRowLayout(12, 4, 22, 4, false),
                new HexRowLayout(16, 8, 26, 8, false),
                new HexRowLayout(24, 8, 34, 8, false),
                new HexRowLayout(32, 8, 42, 8, false),
            ],
            rows);
    }

    [Fact]
    public void ContextKeepsRowsAroundDifferencesAndCountsTheRest()
    {
        var left = new byte[16 * 20];
        var right = (byte[])left.Clone();
        right[16 * 5] = 1;
        right[16 * 6] = 1;
        right[16 * 15] = 1;
        var result = BinaryComparer.Compare(left, right);
        using var leftStream = new MemoryStream(left);
        using var rightStream = new MemoryStream(right);
        var pairs = HexPairedView.Build(leftStream, rightStream, result, new HexFormatter(), 1).ToList();
        var shape = pairs.Select(pair => pair.IsGap ? "gap" + pair.SkippedRows : pair.HasDifference ? "diff" : "row").ToList();
        Assert.Equal(["gap4", "row", "diff", "diff", "row", "gap6", "row", "diff", "row", "gap3"], shape);
        Assert.Equal(0x50, pairs[2].Left!.Offset);

        var all = HexPairedView.Build(leftStream, rightStream, result, new HexFormatter()).ToList();
        Assert.Equal(20, all.Count);
        Assert.DoesNotContain(all, pair => pair.IsGap);

        Assert.Throws<ArgumentException>(() => HexPairedView.Build(new NonSeekable(), rightStream, result, new HexFormatter()));
    }

    [Fact]
    public void EmptyAndEqualFilesHaveSimpleLayouts()
    {
        Assert.Empty(HexPairedView.Layout(BinaryComparer.Compare([], []), 16));
        var inserted = HexPairedView.Layout(BinaryComparer.Compare([], [1, 2, 3]), 16).ToList();
        Assert.Equal([new HexRowLayout(0, 0, 0, 3, true)], inserted);
        var equal = HexPairedView.Layout(BinaryComparer.Compare(new byte[33], new byte[33]), 16).ToList();
        Assert.Equal(3, equal.Count);
        Assert.All(equal, row => Assert.False(row.HasDifference));
    }

    /// <summary>Builds a result with hand-picked ranges, as a resync compare would.</summary>
    private sealed class BinaryDiffResultBuilder(long leftLength, long rightLength)
    {
        private readonly List<BinaryDiffRange> _ranges = [];

        public BinaryDiffResultBuilder Add(long leftOffset, long leftLength, long rightOffset, long rightLength)
        {
            _ranges.Add(BinaryDiffRange.Create(leftOffset, leftLength, rightOffset, rightLength));
            return this;
        }

        public BinaryDiffResult Build() => new(_ranges, leftLength, rightLength, BinaryCompareMode.Resync, null);
    }

    private sealed class NonSeekable : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
