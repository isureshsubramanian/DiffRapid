using System;
using System.Collections.Generic;

namespace DiffRapid.Core.Imaging;

/// <summary>How one pixel of the comparison canvas compares.</summary>
public enum PixelDiffKind : byte
{
    /// <summary>Neither image covers the pixel (a corner left uncovered by an offset).</summary>
    Empty,

    /// <summary>Both images cover the pixel and the colours match within the tolerance.</summary>
    Same,

    /// <summary>Both images cover the pixel and the colours differ.</summary>
    Different,

    /// <summary>Only the left image covers the pixel.</summary>
    LeftOnly,

    /// <summary>Only the right image covers the pixel.</summary>
    RightOnly,
}

/// <summary>A rectangle in canvas pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct ImageRegion(int X, int Y, int Width, int Height)
{
    /// <summary>One past the right edge.</summary>
    public int Right => X + Width;

    /// <summary>One past the bottom edge.</summary>
    public int Bottom => Y + Height;

    /// <summary>True when the rectangles share at least one pixel.</summary>
    public bool Intersects(ImageRegion other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>The smallest rectangle containing both.</summary>
    public ImageRegion Union(ImageRegion other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        return new ImageRegion(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }
}

/// <summary>
/// The result of <see cref="ImageComparer.Compare"/>. Pixels are reported on a canvas that holds both images:
/// the left image at (<see cref="LeftX"/>, <see cref="LeftY"/>) and the right image at
/// (<see cref="RightX"/>, <see cref="RightY"/>). Without an offset both sit at (0, 0) and the canvas is the
/// larger width by the larger height.
/// </summary>
public sealed class ImageDiffResult
{
    private readonly PixelDiffKind[] _kinds;

    internal ImageDiffResult(ImageCanvas canvas, PixelDiffKind[] kinds, int blockSize, IReadOnlyList<ImageRegion> regions)
    {
        _kinds = kinds;
        Width = canvas.Width;
        Height = canvas.Height;
        LeftX = canvas.LeftX;
        LeftY = canvas.LeftY;
        RightX = canvas.RightX;
        RightY = canvas.RightY;
        LeftWidth = canvas.LeftWidth;
        LeftHeight = canvas.LeftHeight;
        RightWidth = canvas.RightWidth;
        RightHeight = canvas.RightHeight;
        BlockSize = blockSize;
        DifferenceRegions = regions;
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case PixelDiffKind.Same:
                    SamePixelCount++;
                    break;
                case PixelDiffKind.Different:
                    DifferentPixelCount++;
                    break;
                case PixelDiffKind.LeftOnly:
                case PixelDiffKind.RightOnly:
                    UnmatchedPixelCount++;
                    break;
            }
        }
    }

    /// <summary>Canvas width in pixels.</summary>
    public int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; }

    /// <summary>Canvas column of the left image's first pixel.</summary>
    public int LeftX { get; }

    /// <summary>Canvas row of the left image's first pixel.</summary>
    public int LeftY { get; }

    /// <summary>Canvas column of the right image's first pixel.</summary>
    public int RightX { get; }

    /// <summary>Canvas row of the right image's first pixel.</summary>
    public int RightY { get; }

    /// <summary>Width of the left image.</summary>
    public int LeftWidth { get; }

    /// <summary>Height of the left image.</summary>
    public int LeftHeight { get; }

    /// <summary>Width of the right image.</summary>
    public int RightWidth { get; }

    /// <summary>Height of the right image.</summary>
    public int RightHeight { get; }

    /// <summary>Horizontal shift of the right image relative to the left (non-zero only after an offset search).</summary>
    public int OffsetX => RightX - LeftX;

    /// <summary>Vertical shift of the right image relative to the left (non-zero only after an offset search).</summary>
    public int OffsetY => RightY - LeftY;

    /// <summary>True when the images have the same width and height.</summary>
    public bool SameSize => LeftWidth == RightWidth && LeftHeight == RightHeight;

    /// <summary>Side of the grid cells used to build <see cref="DifferenceRegions"/>.</summary>
    public int BlockSize { get; }

    /// <summary>Pixels both images cover whose colours match.</summary>
    public long SamePixelCount { get; }

    /// <summary>Pixels both images cover whose colours differ.</summary>
    public long DifferentPixelCount { get; }

    /// <summary>Pixels only one image covers.</summary>
    public long UnmatchedPixelCount { get; }

    /// <summary>All differing pixels: <see cref="DifferentPixelCount"/> plus <see cref="UnmatchedPixelCount"/>.</summary>
    public long DifferenceCount => DifferentPixelCount + UnmatchedPixelCount;

    /// <summary>Pixels covered by at least one image.</summary>
    public long ComparedPixelCount => SamePixelCount + DifferenceCount;

    /// <summary>Differing pixels as a percentage (0 to 100) of the pixels covered by at least one image.</summary>
    public double DifferencePercentage => ComparedPixelCount == 0 ? 0 : DifferenceCount * 100.0 / ComparedPixelCount;

    /// <summary>True when every covered pixel matches.</summary>
    public bool AreIdentical => DifferenceCount == 0;

    /// <summary>
    /// Rectangles around the differing blocks, in canvas pixels, ordered top to bottom then left to right.
    /// Touching blocks are joined, and overlapping rectangles merged, so the rectangles never overlap.
    /// </summary>
    public IReadOnlyList<ImageRegion> DifferenceRegions { get; }

    /// <summary>How the canvas pixel at a position compares.</summary>
    public PixelDiffKind GetKind(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return _kinds[(long)y * Width + x];
    }

    /// <summary>True when the canvas pixel differs or is covered by one image only.</summary>
    public bool IsDifference(int x, int y) => IsDifference(GetKind(x, y));

    internal static bool IsDifference(PixelDiffKind kind) => kind is PixelDiffKind.Different or PixelDiffKind.LeftOnly or PixelDiffKind.RightOnly;

    internal PixelDiffKind KindAt(long index) => _kinds[index];

    internal void CheckImages(PixelImage left, PixelImage right)
    {
        if (left.Width != LeftWidth || left.Height != LeftHeight || right.Width != RightWidth || right.Height != RightHeight)
        {
            throw new ArgumentException("The images are not the ones this comparison was made from.");
        }
    }
}
