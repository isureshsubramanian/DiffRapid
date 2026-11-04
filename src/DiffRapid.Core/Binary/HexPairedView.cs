using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DiffRapid.Core.Binary;

/// <summary>
/// Where one row of a side-by-side hex view reads its bytes. A count of zero on a side makes that side a
/// filler line, shown where the other side has inserted or extra bytes.
/// </summary>
public sealed record HexRowLayout(long LeftOffset, int LeftCount, long RightOffset, int RightCount, bool HasDifference);

/// <summary>A row of a side-by-side hex view, or a gap standing for rows left out around the differences.</summary>
public sealed class HexLinePair
{
    internal HexLinePair(HexLine? left, HexLine? right, bool hasDifference, long skippedRows)
    {
        Left = left;
        Right = right;
        HasDifference = hasDifference;
        SkippedRows = skippedRows;
    }

    /// <summary>The left line, or null for a filler line or a gap.</summary>
    public HexLine? Left { get; }

    /// <summary>The right line, or null for a filler line or a gap.</summary>
    public HexLine? Right { get; }

    /// <summary>Whether this row contains differing bytes.</summary>
    public bool HasDifference { get; }

    /// <summary>For a gap, the number of rows it stands for; otherwise zero.</summary>
    public long SkippedRows { get; }

    /// <summary>Whether this entry is a gap rather than a row.</summary>
    public bool IsGap => SkippedRows > 0;
}

/// <summary>
/// Lines up the left and right files of a <see cref="BinaryDiffResult"/> for a side-by-side hex view. Rows
/// follow the left file's line grid; where a resync compare found an insertion or deletion, the shorter
/// side gets filler lines so the bytes after it line up again.
/// </summary>
public static class HexPairedView
{
    /// <summary>
    /// The rows of the view, computed from the ranges alone without reading the files. The sequence is lazy,
    /// so very large files can be laid out without holding every row.
    /// </summary>
    public static IEnumerable<HexRowLayout> Layout(BinaryDiffResult result, int bytesPerLine)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (bytesPerLine <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerLine));
        }

        return LayoutRows(result, bytesPerLine);
    }

    /// <summary>
    /// Builds the formatted rows. Both streams must be seekable; they are read only for the rows returned.
    /// With <paramref name="contextRows"/>, only rows with differences and that many rows around them are
    /// returned, and each run of rows left out becomes one gap entry.
    /// </summary>
    public static IEnumerable<HexLinePair> Build(Stream left, Stream right, BinaryDiffResult result, HexFormatter formatter,
        int? contextRows = null, CancellationToken cancellationToken = default)
    {
        if (left is null || !left.CanSeek)
        {
            throw new ArgumentException("The left stream must be seekable.", nameof(left));
        }

        if (right is null || !right.CanSeek)
        {
            throw new ArgumentException("The right stream must be seekable.", nameof(right));
        }

        if (formatter is null)
        {
            throw new ArgumentNullException(nameof(formatter));
        }

        if (contextRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextRows));
        }

        var rows = Layout(result, formatter.Options.BytesPerLine);
        return BuildRows(left, right, result, formatter, rows, contextRows, cancellationToken);
    }

    /// <summary>Reads and formats one row of a layout; used by views that only show the rows on screen.</summary>
    public static HexLinePair FormatRow(Stream left, Stream right, HexRowLayout row, BinaryDiffResult result, HexFormatter formatter)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (formatter is null)
        {
            throw new ArgumentNullException(nameof(formatter));
        }

        var leftLine = ReadLine(left, row.LeftOffset, row.LeftCount, result.Ranges, BinarySide.Left, formatter);
        var rightLine = ReadLine(right, row.RightOffset, row.RightCount, result.Ranges, BinarySide.Right, formatter);
        return new HexLinePair(leftLine, rightLine, row.HasDifference, 0);
    }

    private static IEnumerable<HexLinePair> BuildRows(Stream left, Stream right, BinaryDiffResult result, HexFormatter formatter,
        IEnumerable<HexRowLayout> rows, int? contextRows, CancellationToken cancellationToken)
    {
        var selected = contextRows is { } context ? WithContext(rows, context) : WithoutGaps(rows);
        foreach (var (row, skipped) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null)
            {
                yield return new HexLinePair(null, null, false, skipped);
                continue;
            }

            yield return FormatRow(left, right, row, result, formatter);
        }
    }

    private static IEnumerable<(HexRowLayout? Row, long Skipped)> WithoutGaps(IEnumerable<HexRowLayout> rows)
    {
        foreach (var row in rows)
        {
            yield return (row, 0);
        }
    }

    /// <summary>Keeps differing rows and <paramref name="context"/> rows on each side; the rest become gaps.</summary>
    private static IEnumerable<(HexRowLayout? Row, long Skipped)> WithContext(IEnumerable<HexRowLayout> rows, int context)
    {
        var before = new Queue<HexRowLayout>();
        long skipped = 0;
        var after = 0;
        foreach (var row in rows)
        {
            if (row.HasDifference)
            {
                if (skipped > 0)
                {
                    yield return (null, skipped);
                    skipped = 0;
                }

                while (before.Count > 0)
                {
                    yield return (before.Dequeue(), 0);
                }

                yield return (row, 0);
                after = context;
                continue;
            }

            if (after > 0)
            {
                after--;
                yield return (row, 0);
                continue;
            }

            before.Enqueue(row);
            if (before.Count > context)
            {
                before.Dequeue();
                skipped++;
            }
        }

        skipped += before.Count;
        if (skipped > 0)
        {
            yield return (null, skipped);
        }
    }

    private static IEnumerable<HexRowLayout> LayoutRows(BinaryDiffResult result, int bytesPerLine)
    {
        var ranges = result.Ranges;
        long blockStart = 0;
        long shift = 0;
        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (StaysInBlock(result, i, shift))
            {
                continue;
            }

            foreach (var row in AlignedRows(result, blockStart, range.LeftOffset, shift, bytesPerLine))
            {
                yield return row;
            }

            foreach (var row in UnbalancedRows(range, bytesPerLine))
            {
                yield return row;
            }

            blockStart = range.LeftOffset + range.LeftLength;
            shift = range.RightOffset + range.RightLength - blockStart;
        }

        var end = Math.Max(result.LeftLength, result.RightLength - shift);
        foreach (var row in AlignedRows(result, blockStart, end, shift, bytesPerLine))
        {
            yield return row;
        }
    }

    /// <summary>
    /// A range keeps the current line grid when it has the same length on both sides, or when it is the
    /// tail of the longer file (an aligned compare's size difference).
    /// </summary>
    private static bool StaysInBlock(BinaryDiffResult result, int index, long shift)
    {
        var range = result.Ranges[index];
        if (range.RightOffset - range.LeftOffset != shift)
        {
            return false;
        }

        if (range.LeftLength == range.RightLength)
        {
            return true;
        }

        var isLast = index == result.Ranges.Count - 1;
        return isLast
            && range.LeftOffset + range.LeftLength == result.LeftLength
            && range.RightOffset + range.RightLength == result.RightLength;
    }

    /// <summary>
    /// Rows where right offset = left position + <paramref name="shift"/>. Positions run in left coordinates
    /// and may pass the end of the left file when the right file is longer.
    /// </summary>
    private static IEnumerable<HexRowLayout> AlignedRows(BinaryDiffResult result, long start, long end, long shift, int bytesPerLine)
    {
        var position = start;
        while (position < end)
        {
            var next = Math.Min(end, ((position / bytesPerLine) + 1) * bytesPerLine);
            var (leftOffset, leftCount) = Clip(position, next, result.LeftLength);
            var (rightOffset, rightCount) = Clip(position + shift, next + shift, result.RightLength);
            var hasDifference = Overlaps(result.Ranges, BinarySide.Left, leftOffset, leftCount)
                || Overlaps(result.Ranges, BinarySide.Right, rightOffset, rightCount);
            yield return new HexRowLayout(leftOffset, leftCount, rightOffset, rightCount, hasDifference);
            position = next;
        }
    }

    /// <summary>Rows for an insertion, deletion or a change of different lengths; the shorter side runs out into filler.</summary>
    private static IEnumerable<HexRowLayout> UnbalancedRows(BinaryDiffRange range, int bytesPerLine)
    {
        var longest = Math.Max(range.LeftLength, range.RightLength);
        for (long done = 0; done < longest; done += bytesPerLine)
        {
            var leftCount = (int)Math.Max(0, Math.Min(bytesPerLine, range.LeftLength - done));
            var rightCount = (int)Math.Max(0, Math.Min(bytesPerLine, range.RightLength - done));
            var leftOffset = range.LeftOffset + Math.Min(done, range.LeftLength);
            var rightOffset = range.RightOffset + Math.Min(done, range.RightLength);
            yield return new HexRowLayout(leftOffset, leftCount, rightOffset, rightCount, true);
        }
    }

    private static (long Offset, int Count) Clip(long start, long end, long length)
    {
        var clippedStart = Math.Min(start, length);
        var clippedEnd = Math.Min(end, length);
        return (clippedStart, (int)(clippedEnd - clippedStart));
    }

    private static bool Overlaps(IReadOnlyList<BinaryDiffRange> ranges, BinarySide side, long offset, int count)
    {
        if (count == 0)
        {
            return false;
        }

        var end = offset + count;
        for (var i = HexFormatter.FirstRangeEndingAfter(ranges, side, offset); i < ranges.Count; i++)
        {
            if (ranges[i].OffsetOn(side) >= end)
            {
                return false;
            }

            if (ranges[i].LengthOn(side) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static HexLine? ReadLine(Stream stream, long offset, int count, IReadOnlyList<BinaryDiffRange> ranges, BinarySide side, HexFormatter formatter)
    {
        if (count == 0)
        {
            return null;
        }

        var buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0)
            {
                throw new EndOfStreamException("The file is shorter than when it was compared.");
            }

            total += read;
        }

        return formatter.FormatLine(offset, buffer, 0, count, ranges, side);
    }
}
