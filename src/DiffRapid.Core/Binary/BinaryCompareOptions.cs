using System;

namespace DiffRapid.Core.Binary;

/// <summary>How two binary files are lined up against each other.</summary>
public enum BinaryCompareMode
{
    /// <summary>Bytes are compared at equal offsets. Streams, so any size works.</summary>
    Aligned,

    /// <summary>
    /// Insertions and deletions are detected so the bytes after them line up again. Both files are read
    /// into memory, up to <see cref="BinaryCompareOptions.ResyncSizeLimit"/>.
    /// </summary>
    Resync,
}

/// <summary>Options for <see cref="BinaryComparer"/>. Immutable; use <c>with</c> to derive variants.</summary>
public sealed record BinaryCompareOptions
{
    /// <summary>The default largest file size, in bytes, compared in <see cref="BinaryCompareMode.Resync"/> mode.</summary>
    public const long DefaultResyncSizeLimit = 64L * 1024 * 1024;

    private readonly long _resyncSizeLimit = DefaultResyncSizeLimit;
    private readonly int _bufferSize = 64 * 1024;

    /// <summary>The default options: aligned compare.</summary>
    public static BinaryCompareOptions Default { get; } = new();

    /// <summary>How the files are lined up.</summary>
    public BinaryCompareMode Mode { get; init; } = BinaryCompareMode.Aligned;

    /// <summary>
    /// The largest size, in bytes, either file may have for a resync compare. Larger files are compared
    /// in aligned mode and the result says so.
    /// </summary>
    public long ResyncSizeLimit
    {
        get => _resyncSizeLimit;
        init => _resyncSizeLimit = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The limit cannot be negative.");
    }

    /// <summary>The size of each read buffer used while streaming.</summary>
    public int BufferSize
    {
        get => _bufferSize;
        init => _bufferSize = value >= 16 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The buffer must hold at least 16 bytes.");
    }
}
