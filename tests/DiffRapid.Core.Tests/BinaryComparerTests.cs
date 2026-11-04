using DiffRapid.Cli;
using DiffRapid.Core.Binary;

namespace DiffRapid.Core.Tests;

public sealed class BinaryComparerTests
{
    private static readonly BinaryCompareOptions Resync = new() { Mode = BinaryCompareMode.Resync };

    [Theory]
    [InlineData(BinaryCompareMode.Aligned)]
    [InlineData(BinaryCompareMode.Resync)]
    public void EqualFilesHaveNoRanges(BinaryCompareMode mode)
    {
        var data = Random(5000, 1);
        var result = BinaryComparer.Compare(data, (byte[])data.Clone(), new BinaryCompareOptions { Mode = mode });
        Assert.True(result.AreEqual);
        Assert.Equal(0, result.DifferenceCount);
        Assert.Equal(5000, result.LeftLength);
        Assert.Equal(5000, result.RightLength);
        Assert.Equal(mode, result.Mode);
        Assert.False(result.FellBackToAligned);
    }

    [Theory]
    [InlineData(BinaryCompareMode.Aligned)]
    [InlineData(BinaryCompareMode.Resync)]
    public void EmptyFiles(BinaryCompareMode mode)
    {
        var options = new BinaryCompareOptions { Mode = mode };
        Assert.True(BinaryComparer.Compare([], [], options).AreEqual);
        var inserted = BinaryComparer.Compare([], [1, 2, 3], options);
        Assert.Equal([new BinaryDiffRange(0, 0, 0, 3, BinaryDiffKind.Inserted)], inserted.Ranges);
        var deleted = BinaryComparer.Compare([1, 2], [], options);
        Assert.Equal([new BinaryDiffRange(0, 2, 0, 0, BinaryDiffKind.Deleted)], deleted.Ranges);
        Assert.Equal(2, deleted.LeftDifferentBytes);
        Assert.Equal(0, deleted.RightDifferentBytes);
    }

    [Theory]
    [InlineData(BinaryCompareMode.Aligned)]
    [InlineData(BinaryCompareMode.Resync)]
    public void OneByteChanged(BinaryCompareMode mode)
    {
        var left = Random(3000, 2);
        var right = (byte[])left.Clone();
        right[1234] ^= 0xFF;
        var result = BinaryComparer.Compare(left, right, new BinaryCompareOptions { Mode = mode });
        Assert.Equal([new BinaryDiffRange(1234, 1, 1234, 1, BinaryDiffKind.Changed)], result.Ranges);
    }

    [Fact]
    public void AlignedMergesNeighbouringBytesAndReportsTheSizeDifference()
    {
        byte[] left = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] right = [1, 0, 0, 4, 5, 6, 7, 8, 0, 10, 11, 12];
        var result = BinaryComparer.Compare(left, right);
        Assert.Equal(
            [
                new BinaryDiffRange(1, 2, 1, 2, BinaryDiffKind.Changed),
                new BinaryDiffRange(8, 1, 8, 1, BinaryDiffKind.Changed),
                new BinaryDiffRange(10, 0, 10, 2, BinaryDiffKind.Inserted),
            ],
            result.Ranges);
        Assert.Equal(3, result.LeftDifferentBytes);
        Assert.Equal(5, result.RightDifferentBytes);
    }

    [Fact]
    public void SizeOnlyDifferenceIsATailRange()
    {
        var right = Random(100, 3);
        var left = right.Concat(new byte[] { 7, 7, 7 }).ToArray();
        var aligned = BinaryComparer.Compare(left, right);
        Assert.Equal([new BinaryDiffRange(100, 3, 100, 0, BinaryDiffKind.Deleted)], aligned.Ranges);
        var resync = BinaryComparer.Compare(left, right, Resync);
        Assert.Equal([new BinaryDiffRange(100, 3, 100, 0, BinaryDiffKind.Deleted)], resync.Ranges);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20000)]
    [InlineData(40000)]
    public void ResyncFindsAnInsertion(int position)
    {
        var left = Random(40000, 4);
        byte[] insert = [0xDE, 0xAD, 0xBE, 0xEF, 0x01];
        var right = left.Take(position).Concat(insert).Concat(left.Skip(position)).ToArray();
        var result = BinaryComparer.Compare(left, right, Resync);
        Assert.Equal(BinaryCompareMode.Resync, result.Mode);
        var range = Assert.Single(result.Ranges);
        Assert.Equal(BinaryDiffKind.Inserted, range.Kind);
        Assert.Equal(0, range.LeftLength);
        Assert.Equal(5, range.RightLength);
        Assert.Equal(range.LeftOffset, range.RightOffset);
        Assert.Equal(insert, right.Skip((int)range.RightOffset).Take(5).ToArray());

        // Aligned mode cannot resync: everything after the insertion differs.
        var aligned = BinaryComparer.Compare(left, right);
        Assert.True(aligned.RightDifferentBytes > 5 || position == 40000);
    }

    [Fact]
    public void ResyncFindsADeletionAndLaterChanges()
    {
        var left = Random(200000, 5);
        var right = left.Take(50000).Concat(left.Skip(50300)).ToArray();
        right[150000] ^= 0x55;
        right[150010] ^= 0x55;
        var result = BinaryComparer.Compare(left, right, Resync);
        Assert.Equal(
            [
                new BinaryDiffRange(50000, 300, 50000, 0, BinaryDiffKind.Deleted),
                new BinaryDiffRange(150300, 1, 150000, 1, BinaryDiffKind.Changed),
                new BinaryDiffRange(150310, 1, 150010, 1, BinaryDiffKind.Changed),
            ],
            result.Ranges);
    }

    [Fact]
    public void ResyncHandlesRepeatedContent()
    {
        // A 1000-byte block repeated 100 times: most chunks occur many times on both sides.
        var block = Random(1000, 8);
        var left = Enumerable.Repeat(block, 100).SelectMany(bytes => bytes).ToArray();
        var inserted = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 + i)).ToArray();
        var right = left.Take(60500).Concat(inserted).Concat(left.Skip(60500)).ToArray();
        right[10] ^= 0xFF;

        var result = BinaryComparer.Compare(left, right, Resync);
        Assert.Equal(2, result.DifferenceCount);
        Assert.Equal(new BinaryDiffRange(10, 1, 10, 1, BinaryDiffKind.Changed), result.Ranges[0]);
        Assert.Equal(BinaryDiffKind.Inserted, result.Ranges[1].Kind);
        Assert.Equal(16, result.Ranges[1].RightLength);
    }

    [Fact]
    public void LargeNonSeekableStreamsCompareInAlignedMode()
    {
        const long length = 48L * 1024 * 1024 + 7;
        using var left = new GeneratedStream(length, 9, []);
        using var right = new GeneratedStream(length, 9, [1, 1_000_000, 1_000_001, 40_000_000, length - 1]);
        var result = BinaryComparer.Compare(left, right);
        Assert.Equal(length, result.LeftLength);
        Assert.Equal(
            [
                new BinaryDiffRange(1, 1, 1, 1, BinaryDiffKind.Changed),
                new BinaryDiffRange(1_000_000, 2, 1_000_000, 2, BinaryDiffKind.Changed),
                new BinaryDiffRange(40_000_000, 1, 40_000_000, 1, BinaryDiffKind.Changed),
                new BinaryDiffRange(length - 1, 1, length - 1, 1, BinaryDiffKind.Changed),
            ],
            result.Ranges);
        Assert.True(left.LargestRead <= BinaryCompareOptions.Default.BufferSize);
    }

    [Fact]
    public void ResyncFallsBackToAlignedAboveTheLimit()
    {
        var left = Random(2000, 6);
        var right = new byte[] { 0 }.Concat(left).ToArray();
        var options = Resync with { ResyncSizeLimit = 1000 };
        var expected = BinaryComparer.Compare(left, right);

        var seekable = BinaryComparer.Compare(left, right, options);
        Assert.Equal(BinaryCompareMode.Aligned, seekable.Mode);
        Assert.True(seekable.FellBackToAligned);
        Assert.Contains("1,000", seekable.Note);
        Assert.Equal(expected.Ranges, seekable.Ranges);

        using var leftStream = new GeneratedStream(left);
        using var rightStream = new GeneratedStream(right);
        var streamed = BinaryComparer.Compare(leftStream, rightStream, options);
        Assert.True(streamed.FellBackToAligned);
        Assert.Equal(expected.Ranges, streamed.Ranges);
        Assert.Equal(2000, streamed.LeftLength);
        Assert.Equal(2001, streamed.RightLength);

        var withinLimit = BinaryComparer.Compare(left, right, Resync with { ResyncSizeLimit = 2001 });
        Assert.Equal(BinaryCompareMode.Resync, withinLimit.Mode);
        Assert.Single(withinLimit.Ranges);
    }

    [Fact]
    public void ComparesFilesOnDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "a.bin");
            var right = Path.Combine(directory, "b.bin");
            File.WriteAllBytes(left, [1, 2, 3]);
            File.WriteAllBytes(right, [1, 9, 3]);
            var result = BinaryComparer.CompareFiles(left, right, Resync);
            Assert.Equal([new BinaryDiffRange(1, 1, 1, 1, BinaryDiffKind.Changed)], result.Ranges);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void HonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var data = Random(1000, 7);
        Assert.Throws<OperationCanceledException>(() => BinaryComparer.Compare(data, data, null, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => BinaryComparer.Compare(data, data, Resync, cancelled.Token));
    }

    [Fact]
    public void RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryCompareOptions { ResyncSizeLimit = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryCompareOptions { BufferSize = 4 });
    }

    internal static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A non-seekable stream of pseudo-random bytes, generated as read, with chosen bytes flipped.</summary>
    private sealed class GeneratedStream : Stream
    {
        private readonly long _length;
        private readonly HashSet<long> _flipped;
        private readonly byte[]? _data;
        private ulong _state;
        private long _position;

        public GeneratedStream(long length, ulong seed, long[] flipped)
        {
            _length = length;
            _state = seed;
            _flipped = [.. flipped];
        }

        public GeneratedStream(byte[] data)
        {
            _data = data;
            _length = data.Length;
            _flipped = [];
        }

        public int LargestRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRead = Math.Max(LargestRead, count);

            // Return short reads now and then, as pipes do.
            var available = (int)Math.Min(Math.Min(count, 7000), _length - _position);
            for (var i = 0; i < available; i++)
            {
                buffer[offset + i] = _data is null ? Next() : _data[_position + i];
                if (_flipped.Contains(_position + i))
                {
                    buffer[offset + i] ^= 0x80;
                }
            }

            _position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private byte Next()
        {
            _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
            return (byte)(_state >> 56);
        }
    }
}

public sealed class BinaryCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diffrapid-hex-" + Guid.NewGuid());

    public BinaryCliTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    [Fact]
    public void PrintsDifferingRowsSideBySide()
    {
        var left = BinaryComparerTests.Random(256, 11);
        var right = (byte[])left.Clone();
        right[100] = 0x41;
        var (exit, output) = Run(Write("a", left), Write("b", right), "--context", "0");
        Assert.Equal(1, exit);
        Assert.Contains("Mode:  aligned", output);
        Assert.Contains("Differences: 1 (1 left bytes, 1 right bytes)", output);
        var rows = output.Split(Environment.NewLine).Where(line => line.StartsWith("* ", StringComparison.Ordinal)).ToList();
        var row = Assert.Single(rows);
        Assert.StartsWith("* 00000060  ", row);
        Assert.Contains(" | 00000060  ", row);
        Assert.Contains("  ... 6 identical rows", output);
        Assert.Contains("  ... 9 identical rows", output);
    }

    [Fact]
    public void ResyncShowsFillerForInsertedBytes()
    {
        var left = BinaryComparerTests.Random(4096, 12);
        var right = left.Take(2000).Concat(new byte[20]).Concat(left.Skip(2000)).ToArray();
        var (exit, output) = Run(Write("a", left), Write("b", right), "--resync", "--bytes-per-line", "8", "--context", "1");
        Assert.Equal(1, exit);
        Assert.Contains("Mode:  resync", output);
        var fillerRows = output.Split(Environment.NewLine).Where(line => line.StartsWith("* ", StringComparison.Ordinal) && line[2..].TrimStart().StartsWith("|", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, fillerRows.Count);
    }

    [Fact]
    public void SummaryAndExitCodes()
    {
        var data = BinaryComparerTests.Random(64, 13);
        var same = Run(Write("a", data), Write("b", data));
        Assert.Equal(0, same.Exit);
        Assert.Contains("Files are identical.", same.Output);

        var summary = Run(Write("a", data), Write("c", data.Take(60).ToArray()), "--summary");
        Assert.Equal(1, summary.Exit);
        Assert.Contains("Differences: 1 (4 left bytes, 0 right bytes)", summary.Output);
        Assert.DoesNotContain("| ", summary.Output);

        Assert.Equal(0, Program.Main(["hex", Write("a", data), Write("b", data), "--summary"]));
        Assert.Equal(1, Program.Main(["hex", Write("a", data), Write("c", [1]), "--summary"]));
        Assert.Equal(2, Program.Main(["hex", Write("a", data)]));
        Assert.Equal(2, Program.Main(["hex", Write("a", data), Path.Combine(_root, "missing")]));
        Assert.Equal(2, Program.Main(["hex", Write("a", data), _root]));
        Assert.Equal(2, Program.Main(["hex", Write("a", data), Write("b", data), "--bytes-per-line", "12"]));
    }

    private (int Exit, string Output) Run(params string[] args)
    {
        using var writer = new StringWriter();
        var exit = Program.RunHex(Program.ParsedArgs.Parse(["hex", .. args], 1), writer);
        return (exit, writer.ToString());
    }

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}
