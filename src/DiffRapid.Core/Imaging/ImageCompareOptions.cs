using System;

namespace DiffRapid.Core.Imaging;

/// <summary>How the colour tolerance is measured.</summary>
public enum ColorToleranceMode
{
    /// <summary>Pixels match when no single channel differs by more than the tolerance.</summary>
    PerChannel,

    /// <summary>Pixels match when the straight-line distance between the colours is within the tolerance.</summary>
    Euclidean,
}

/// <summary>Settings for <see cref="ImageComparer"/>.</summary>
public sealed record ImageCompareOptions
{
    /// <summary>The largest Euclidean tolerance: the distance between transparent black and opaque white.</summary>
    public const int MaxEuclideanTolerance = 510;

    /// <summary>The largest per-channel tolerance.</summary>
    public const int MaxChannelTolerance = 255;

    /// <summary>
    /// How far two colours may be apart and still match: 0 to 255 per channel, or 0 to 510 when
    /// <see cref="ToleranceMode"/> is <see cref="ColorToleranceMode.Euclidean"/>. Zero means exact.
    /// </summary>
    public int ColorTolerance { get; init; }

    /// <summary>How <see cref="ColorTolerance"/> is measured.</summary>
    public ColorToleranceMode ToleranceMode { get; init; } = ColorToleranceMode.PerChannel;

    /// <summary>
    /// Compare colour only. When false, alpha is compared too, and two fully transparent pixels always
    /// match whatever their colour bytes hold.
    /// </summary>
    public bool IgnoreAlpha { get; init; }

    /// <summary>Side of the square grid cells that are merged into difference rectangles, in pixels.</summary>
    public int BlockSize { get; init; } = 8;

    /// <summary>
    /// Search for the shift of the right image, up to this many pixels in each direction, that leaves the
    /// fewest differences. Zero turns the search off. The search tries <c>(2K+1)^2</c> shifts, so keep it small.
    /// </summary>
    public int MaxOffset { get; init; }

    /// <summary>The default options: exact match, alpha compared, 8-pixel blocks, no offset search.</summary>
    public static ImageCompareOptions Default { get; } = new();

    internal void Validate()
    {
        var maxTolerance = ToleranceMode == ColorToleranceMode.Euclidean ? MaxEuclideanTolerance : MaxChannelTolerance;
        if (ColorTolerance < 0 || ColorTolerance > maxTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(ColorTolerance), $"The colour tolerance must be between 0 and {maxTolerance}.");
        }

        if (BlockSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize), "The block size must be at least one pixel.");
        }

        if (MaxOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOffset), "The offset search range cannot be negative.");
        }
    }
}
