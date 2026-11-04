using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DiffRapid.Core.Binary;

/// <summary>The order in which the bytes of a group are shown.</summary>
public enum HexByteOrder
{
    /// <summary>Bytes are shown in file order (big-endian reading of a group).</summary>
    BigEndian,

    /// <summary>The bytes of each group are shown last first, so a group reads as a little-endian number.</summary>
    LittleEndian,
}

/// <summary>Layout of a hex view. Immutable; use <c>with</c> to derive variants.</summary>
public sealed record HexFormatOptions
{
    private readonly int _bytesPerLine = 16;
    private readonly int _groupSize = 1;
    private readonly int _offsetDigits = 8;

    /// <summary>The default layout: 16 bytes per line in single-byte groups.</summary>
    public static HexFormatOptions Default { get; } = new();

    /// <summary>Bytes per line: 8, 16 or 32.</summary>
    public int BytesPerLine
    {
        get => _bytesPerLine;
        init => _bytesPerLine = value is 8 or 16 or 32 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Bytes per line must be 8, 16 or 32.");
    }

    /// <summary>Bytes shown together without a space: 1, 2, 4 or 8.</summary>
    public int GroupSize
    {
        get => _groupSize;
        init => _groupSize = value is 1 or 2 or 4 or 8 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The group size must be 1, 2, 4 or 8.");
    }

    /// <summary>The byte order within a group; it only matters for groups of two or more bytes.</summary>
    public HexByteOrder ByteOrder { get; init; } = HexByteOrder.BigEndian;

    /// <summary>Whether hex digits are upper case.</summary>
    public bool UpperCase { get; init; } = true;

    /// <summary>The minimum number of hex digits in the offset column; longer offsets use more.</summary>
    public int OffsetDigits
    {
        get => _offsetDigits;
        init => _offsetDigits = value is >= 1 and <= 16 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Offset digits must be between 1 and 16.");
    }
}

/// <summary>One line of a hex view.</summary>
public sealed class HexLine
{
    internal HexLine(long offset, byte[] bytes, string offsetText, string hex, string ascii, bool[] different)
    {
        Offset = offset;
        Bytes = bytes;
        OffsetText = offsetText;
        Hex = hex;
        Ascii = ascii;
        Different = different;
        HasDifference = Array.IndexOf(different, true) >= 0;
    }

    /// <summary>The file offset of the first byte.</summary>
    public long Offset { get; }

    /// <summary>The bytes on this line; fewer than a full line at the end of a file or a block.</summary>
    public IReadOnlyList<byte> Bytes { get; }

    /// <summary>The offset in hex.</summary>
    public string OffsetText { get; }

    /// <summary>The hex column, padded to <see cref="HexFormatter.HexWidth"/>.</summary>
    public string Hex { get; }

    /// <summary>The bytes as ASCII, with '.' for anything that is not printable.</summary>
    public string Ascii { get; }

    /// <summary>For each byte of <see cref="Bytes"/>, whether it lies inside a differing range.</summary>
    public IReadOnlyList<bool> Different { get; }

    /// <summary>Whether any byte on this line differs.</summary>
    public bool HasDifference { get; }
}

/// <summary>Formats bytes as hex view lines: offset, hex digits in groups, and an ASCII column.</summary>
public sealed class HexFormatter
{
    /// <summary>Creates a formatter with the given layout.</summary>
    public HexFormatter(HexFormatOptions? options = null)
    {
        Options = options ?? HexFormatOptions.Default;
        if (Options.GroupSize > Options.BytesPerLine)
        {
            throw new ArgumentException("The group size cannot exceed the bytes per line.", nameof(options));
        }

        var groups = Options.BytesPerLine / Options.GroupSize;
        HexWidth = (Options.BytesPerLine * 2) + groups - 1;
    }

    /// <summary>The layout in use.</summary>
    public HexFormatOptions Options { get; }

    /// <summary>The width in characters of the hex column.</summary>
    public int HexWidth { get; }

    /// <summary>The character index in <see cref="HexLine.Hex"/> of the first digit of a byte of the line.</summary>
    public int HexColumn(int byteIndex)
    {
        if (byteIndex < 0 || byteIndex >= Options.BytesPerLine)
        {
            throw new ArgumentOutOfRangeException(nameof(byteIndex));
        }

        var group = byteIndex / Options.GroupSize;
        var inGroup = byteIndex % Options.GroupSize;
        var position = Options.ByteOrder == HexByteOrder.LittleEndian ? Options.GroupSize - 1 - inGroup : inGroup;
        return (group * ((Options.GroupSize * 2) + 1)) + (position * 2);
    }

    /// <summary>Formats an offset with at least <see cref="HexFormatOptions.OffsetDigits"/> digits.</summary>
    public string FormatOffset(long offset) =>
        offset.ToString((Options.UpperCase ? "X" : "x") + Options.OffsetDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats one line. <paramref name="count"/> may be less than a full line. When <paramref name="ranges"/>
    /// is given, bytes inside a range on <paramref name="side"/> are marked as different.
    /// </summary>
    public HexLine FormatLine(long offset, byte[] buffer, int index, int count, IReadOnlyList<BinaryDiffRange>? ranges = null, BinarySide side = BinarySide.Left)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (index < 0 || count < 0 || index + count > buffer.Length || count > Options.BytesPerLine)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var bytes = new byte[count];
        Buffer.BlockCopy(buffer, index, bytes, 0, count);
        var different = new bool[count];
        if (ranges is not null)
        {
            MarkDifferences(offset, different, ranges, side);
        }

        return new HexLine(offset, bytes, FormatOffset(offset), FormatHex(bytes), FormatAscii(bytes), different);
    }

    /// <summary>Formats the hex column, padded to <see cref="HexWidth"/>.</summary>
    public string FormatHex(IReadOnlyList<byte> bytes)
    {
        var text = new char[HexWidth];
        for (var i = 0; i < text.Length; i++)
        {
            text[i] = ' ';
        }

        var digits = Options.UpperCase ? "0123456789ABCDEF" : "0123456789abcdef";
        for (var i = 0; i < bytes.Count; i++)
        {
            var column = HexColumn(i);
            text[column] = digits[bytes[i] >> 4];
            text[column + 1] = digits[bytes[i] & 0xF];
        }

        return new string(text);
    }

    /// <summary>Formats the ASCII column; bytes outside 0x20..0x7E show as '.'.</summary>
    public static string FormatAscii(IReadOnlyList<byte> bytes)
    {
        var text = new StringBuilder(bytes.Count);
        foreach (var value in bytes)
        {
            text.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        return text.ToString();
    }

    /// <summary>The whole line as text: offset, hex and ASCII columns separated by two spaces.</summary>
    public string FormatText(HexLine line)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }

        return line.OffsetText + "  " + line.Hex + "  " + line.Ascii;
    }

    private static void MarkDifferences(long offset, bool[] different, IReadOnlyList<BinaryDiffRange> ranges, BinarySide side)
    {
        var end = offset + different.Length;
        for (var i = FirstRangeEndingAfter(ranges, side, offset); i < ranges.Count; i++)
        {
            var start = ranges[i].OffsetOn(side);
            if (start >= end)
            {
                break;
            }

            var stop = Math.Min(end, start + ranges[i].LengthOn(side));
            for (var position = Math.Max(start, offset); position < stop; position++)
            {
                different[position - offset] = true;
            }
        }
    }

    /// <summary>Binary search: ranges are ordered on both sides, so their ends on one side are ordered too.</summary>
    internal static int FirstRangeEndingAfter(IReadOnlyList<BinaryDiffRange> ranges, BinarySide side, long offset)
    {
        var low = 0;
        var high = ranges.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var range = ranges[middle];
            if (range.OffsetOn(side) + range.LengthOn(side) <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
