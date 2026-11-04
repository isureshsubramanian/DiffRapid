using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace DiffRapid.Core.Binary;

/// <summary>Compares two files byte by byte and reports the differing ranges.</summary>
public static class BinaryComparer
{
    private const long MaximumResyncBytes = int.MaxValue - 1024;

    /// <summary>Compares two files on disk.</summary>
    public static BinaryDiffResult CompareFiles(string leftPath, string rightPath, BinaryCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= BinaryCompareOptions.Default;
        using var left = OpenRead(leftPath, options.BufferSize);
        using var right = OpenRead(rightPath, options.BufferSize);
        return Compare(left, right, options, cancellationToken);
    }

    /// <summary>Compares two byte arrays.</summary>
    public static BinaryDiffResult Compare(byte[] left, byte[] right, BinaryCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        using var leftStream = new MemoryStream(left, false);
        using var rightStream = new MemoryStream(right, false);
        return Compare(leftStream, rightStream, options, cancellationToken);
    }

    /// <summary>
    /// Compares two streams from their current positions to their ends. Neither stream needs to be seekable;
    /// in aligned mode they are read once, in buffers, so their size is not limited.
    /// </summary>
    public static BinaryDiffResult Compare(Stream left, Stream right, BinaryCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options ??= BinaryCompareOptions.Default;
        if (options.Mode == BinaryCompareMode.Aligned)
        {
            return CompareAligned(left, right, options.BufferSize, null, cancellationToken);
        }

        return CompareResync(left, right, options, cancellationToken);
    }

    /// <summary>Opens a file for a compare, letting other programs keep writing to it.</summary>
    internal static FileStream OpenRead(string path, int bufferSize) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);

    private static BinaryDiffResult CompareResync(Stream left, Stream right, BinaryCompareOptions options, CancellationToken cancellationToken)
    {
        // Arrays cannot hold more than about 2 GB, whatever the limit says.
        var limit = Math.Min(options.ResyncSizeLimit, MaximumResyncBytes);
        var note = string.Format(
            CultureInfo.InvariantCulture,
            "A file is larger than the resync limit of {0:N0} bytes, so bytes were compared at equal offsets.",
            limit);
        if (IsKnownLarger(left, limit) || IsKnownLarger(right, limit))
        {
            return CompareAligned(left, right, options.BufferSize, note, cancellationToken);
        }

        var leftPrefix = ReadUpTo(left, limit, cancellationToken);
        var rightPrefix = ReadUpTo(right, limit, cancellationToken);
        if (leftPrefix.Length > limit || rightPrefix.Length > limit)
        {
            // The bytes read so far are put back in front of the rest of each stream.
            using var leftRest = new PrefixedStream(leftPrefix, left);
            using var rightRest = new PrefixedStream(rightPrefix, right);
            return CompareAligned(leftRest, rightRest, options.BufferSize, note, cancellationToken);
        }

        var ranges = ResyncDiff.Compare(leftPrefix, rightPrefix, cancellationToken);
        return new BinaryDiffResult(ranges, leftPrefix.Length, rightPrefix.Length, BinaryCompareMode.Resync, null);
    }

    private static bool IsKnownLarger(Stream stream, long limit)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        return stream.Length - stream.Position > limit;
    }

    /// <summary>Reads at most <paramref name="limit"/> + 1 bytes, so a result longer than the limit means "too large".</summary>
    private static byte[] ReadUpTo(Stream stream, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length <= limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(chunk.Length, limit + 1 - buffer.Length);
            var read = stream.Read(chunk, 0, wanted);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static BinaryDiffResult CompareAligned(Stream left, Stream right, int bufferSize, string? note, CancellationToken cancellationToken)
    {
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        var builder = new AlignedRangeBuilder();
        long offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftCount = ReadFull(left, leftBuffer);
            var rightCount = ReadFull(right, rightBuffer);
            var common = Math.Min(leftCount, rightCount);
            builder.Scan(leftBuffer, rightBuffer, common, offset);
            offset += common;
            if (leftCount != rightCount)
            {
                builder.CloseRun(offset);
                var leftLength = offset + (leftCount - common) + Drain(left, leftBuffer, cancellationToken);
                var rightLength = offset + (rightCount - common) + Drain(right, rightBuffer, cancellationToken);
                builder.AddTail(offset, leftLength, rightLength);
                return new BinaryDiffResult(builder.Ranges, leftLength, rightLength, BinaryCompareMode.Aligned, note);
            }

            if (leftCount == 0)
            {
                builder.CloseRun(offset);
                return new BinaryDiffResult(builder.Ranges, offset, offset, BinaryCompareMode.Aligned, note);
            }
        }
    }

    /// <summary>Fills the buffer unless the stream ends first, so both sides stay at the same offset.</summary>
    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static long Drain(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }
    }

    /// <summary>Collects runs of differing bytes at equal offsets.</summary>
    private sealed class AlignedRangeBuilder
    {
        private long _runStart = -1;

        public List<BinaryDiffRange> Ranges { get; } = [];

        public void Scan(byte[] left, byte[] right, int count, long baseOffset)
        {
            var index = 0;
            while (index < count)
            {
                if (_runStart < 0)
                {
                    index = SkipEqual(left, right, index, count);
                    if (index < count)
                    {
                        _runStart = baseOffset + index;
                        index++;
                    }

                    continue;
                }

                if (left[index] == right[index])
                {
                    CloseRun(baseOffset + index);
                }

                index++;
            }
        }

        public void CloseRun(long end)
        {
            if (_runStart < 0)
            {
                return;
            }

            var length = end - _runStart;
            Ranges.Add(new BinaryDiffRange(_runStart, length, _runStart, length, BinaryDiffKind.Changed));
            _runStart = -1;
        }

        public void AddTail(long commonLength, long leftLength, long rightLength)
        {
            if (leftLength == rightLength)
            {
                return;
            }

            Ranges.Add(BinaryDiffRange.Create(commonLength, leftLength - commonLength, commonLength, rightLength - commonLength));
        }

        /// <summary>Returns the index of the first differing byte, comparing eight bytes at a time where possible.</summary>
        private static int SkipEqual(byte[] left, byte[] right, int index, int count)
        {
            while (index + 8 <= count && BitConverter.ToUInt64(left, index) == BitConverter.ToUInt64(right, index))
            {
                index += 8;
            }

            while (index < count && left[index] == right[index])
            {
                index++;
            }

            return index;
        }
    }

    /// <summary>A read-only stream that returns a buffered prefix and then the rest of another stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _prefixPosition;

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
            if (_prefixPosition < prefix.Length)
            {
                var copied = Math.Min(count, prefix.Length - _prefixPosition);
                Buffer.BlockCopy(prefix, _prefixPosition, buffer, offset, copied);
                _prefixPosition += copied;
                return copied;
            }

            return rest.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
