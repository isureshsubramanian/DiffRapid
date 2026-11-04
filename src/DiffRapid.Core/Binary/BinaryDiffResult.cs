using System.Collections.Generic;

namespace DiffRapid.Core.Binary;

/// <summary>What happened to a run of bytes.</summary>
public enum BinaryDiffKind
{
    /// <summary>Bytes on both sides differ.</summary>
    Changed,

    /// <summary>Bytes exist only on the right.</summary>
    Inserted,

    /// <summary>Bytes exist only on the left.</summary>
    Deleted,
}

/// <summary>Which file of a binary comparison.</summary>
public enum BinarySide
{
    Left,
    Right,
}

/// <summary>
/// A run of differing bytes. Offsets are zero-based; a side with length zero marks the position where the
/// other side's bytes were inserted or deleted.
/// </summary>
public sealed record BinaryDiffRange(long LeftOffset, long LeftLength, long RightOffset, long RightLength, BinaryDiffKind Kind)
{
    /// <summary>The offset of this range on one side.</summary>
    public long OffsetOn(BinarySide side) => side == BinarySide.Left ? LeftOffset : RightOffset;

    /// <summary>The length of this range on one side.</summary>
    public long LengthOn(BinarySide side) => side == BinarySide.Left ? LeftLength : RightLength;

    internal static BinaryDiffRange Create(long leftOffset, long leftLength, long rightOffset, long rightLength)
    {
        var kind = leftLength == 0 ? BinaryDiffKind.Inserted : rightLength == 0 ? BinaryDiffKind.Deleted : BinaryDiffKind.Changed;
        return new BinaryDiffRange(leftOffset, leftLength, rightOffset, rightLength, kind);
    }
}

/// <summary>The outcome of a binary comparison: the differing ranges in file order, plus totals.</summary>
public sealed class BinaryDiffResult
{
    internal BinaryDiffResult(IReadOnlyList<BinaryDiffRange> ranges, long leftLength, long rightLength, BinaryCompareMode mode, string? note)
    {
        Ranges = ranges;
        LeftLength = leftLength;
        RightLength = rightLength;
        Mode = mode;
        Note = note;
        foreach (var range in ranges)
        {
            LeftDifferentBytes += range.LeftLength;
            RightDifferentBytes += range.RightLength;
        }
    }

    /// <summary>The differing ranges, ordered by offset on both sides.</summary>
    public IReadOnlyList<BinaryDiffRange> Ranges { get; }

    /// <summary>The size of the left file in bytes.</summary>
    public long LeftLength { get; }

    /// <summary>The size of the right file in bytes.</summary>
    public long RightLength { get; }

    /// <summary>The mode actually used; aligned when a resync compare fell back because of the size limit.</summary>
    public BinaryCompareMode Mode { get; }

    /// <summary>Why the mode differs from the one requested, or null.</summary>
    public string? Note { get; }

    /// <summary>Whether the requested resync compare fell back to aligned mode.</summary>
    public bool FellBackToAligned => Note is not null;

    /// <summary>Whether the files are byte for byte identical.</summary>
    public bool AreEqual => Ranges.Count == 0;

    /// <summary>The number of differing ranges.</summary>
    public int DifferenceCount => Ranges.Count;

    /// <summary>The number of left bytes inside differing ranges.</summary>
    public long LeftDifferentBytes { get; }

    /// <summary>The number of right bytes inside differing ranges.</summary>
    public long RightDifferentBytes { get; }
}
