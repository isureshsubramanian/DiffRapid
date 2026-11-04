using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DiffRapid.Core.Imaging;

/// <summary>Compares two decoded images pixel by pixel.</summary>
public static class ImageComparer
{
    /// <summary>
    /// Compares two images. Images of different sizes are compared over the area they share; pixels covered by
    /// only one image count as differences. With <see cref="ImageCompareOptions.MaxOffset"/> set, the right image
    /// is first shifted to the position that leaves the fewest differences.
    /// </summary>
    /// <param name="left">The left image.</param>
    /// <param name="right">The right image.</param>
    /// <param name="options">Comparison settings, or null for <see cref="ImageCompareOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops a long comparison or offset search.</param>
    public static ImageDiffResult Compare(PixelImage left, PixelImage right, ImageCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        options ??= ImageCompareOptions.Default;
        options.Validate();
        var matcher = new PixelMatcher(options);
        var (offsetX, offsetY) = FindBestOffset(left, right, matcher, options.MaxOffset, cancellationToken);
        var canvas = ImageCanvas.Create(left, right, offsetX, offsetY);
        var kinds = Classify(left, right, canvas, matcher, cancellationToken);
        var regions = DifferenceBlocks.FindRegions(kinds, canvas.Width, canvas.Height, options.BlockSize, cancellationToken);
        return new ImageDiffResult(canvas, kinds, options.BlockSize, regions);
    }

    private static PixelDiffKind[] Classify(PixelImage left, PixelImage right, ImageCanvas canvas, PixelMatcher matcher, CancellationToken cancellationToken)
    {
        var kinds = new PixelDiffKind[(long)canvas.Width * canvas.Height];
        for (var y = 0; y < canvas.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = (long)y * canvas.Width;
            for (var x = 0; x < canvas.Width; x++)
            {
                kinds[rowStart + x] = ClassifyPixel(left, right, canvas, matcher, x, y);
            }
        }

        return kinds;
    }

    private static PixelDiffKind ClassifyPixel(PixelImage left, PixelImage right, ImageCanvas canvas, PixelMatcher matcher, int x, int y)
    {
        var inLeft = canvas.InLeft(x, y);
        var inRight = canvas.InRight(x, y);
        if (inLeft && inRight)
        {
            var leftIndex = left.IndexOf(x - canvas.LeftX, y - canvas.LeftY);
            var rightIndex = right.IndexOf(x - canvas.RightX, y - canvas.RightY);
            return matcher.Matches(left.Pixels, leftIndex, right.Pixels, rightIndex) ? PixelDiffKind.Same : PixelDiffKind.Different;
        }

        if (inLeft)
        {
            return PixelDiffKind.LeftOnly;
        }

        return inRight ? PixelDiffKind.RightOnly : PixelDiffKind.Empty;
    }

    /// <summary>
    /// Tries every shift of the right image within +/- <paramref name="maxOffset"/> and returns the one with the
    /// fewest differences (unmatched pixels included). Ties go to the shift closest to no shift.
    /// </summary>
    private static (int X, int Y) FindBestOffset(PixelImage left, PixelImage right, PixelMatcher matcher, int maxOffset, CancellationToken cancellationToken)
    {
        if (maxOffset == 0 || left.PixelCount == 0 || right.PixelCount == 0)
        {
            return (0, 0);
        }

        var best = (X: 0, Y: 0);
        var bestScore = long.MaxValue;
        foreach (var (offsetX, offsetY) in CandidateOffsets(maxOffset))
        {
            var score = ScoreOffset(left, right, matcher, offsetX, offsetY, bestScore, cancellationToken);
            if (score < bestScore)
            {
                bestScore = score;
                best = (offsetX, offsetY);
            }
        }

        return best;
    }

    private static IEnumerable<(int X, int Y)> CandidateOffsets(int maxOffset)
    {
        var candidates = new List<(int X, int Y)>();
        for (var y = -maxOffset; y <= maxOffset; y++)
        {
            for (var x = -maxOffset; x <= maxOffset; x++)
            {
                candidates.Add((x, y));
            }
        }

        return candidates.OrderBy(offset => Math.Abs(offset.X) + Math.Abs(offset.Y)).ThenBy(offset => offset.Y).ThenBy(offset => offset.X);
    }

    /// <summary>Counts differences for one shift, stopping early once the count reaches <paramref name="limit"/>.</summary>
    private static long ScoreOffset(PixelImage left, PixelImage right, PixelMatcher matcher, int offsetX, int offsetY, long limit, CancellationToken cancellationToken)
    {
        var overlapLeft = Math.Max(0, offsetX);
        var overlapRight = Math.Min(left.Width, offsetX + right.Width);
        var overlapTop = Math.Max(0, offsetY);
        var overlapBottom = Math.Min(left.Height, offsetY + right.Height);
        var overlapWidth = Math.Max(0, overlapRight - overlapLeft);
        var overlapHeight = Math.Max(0, overlapBottom - overlapTop);
        var overlap = (long)overlapWidth * overlapHeight;
        var score = left.PixelCount + right.PixelCount - 2 * overlap;
        for (var y = overlapTop; y < overlapTop + overlapHeight && score < limit; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = overlapLeft; x < overlapLeft + overlapWidth; x++)
            {
                var leftIndex = left.IndexOf(x, y);
                var rightIndex = right.IndexOf(x - offsetX, y - offsetY);
                if (!matcher.Matches(left.Pixels, leftIndex, right.Pixels, rightIndex))
                {
                    score++;
                }
            }
        }

        return score;
    }
}
