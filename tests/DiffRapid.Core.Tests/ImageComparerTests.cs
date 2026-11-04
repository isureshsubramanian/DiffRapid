using DiffRapid.Cli;
using DiffRapid.Core.Imaging;

namespace DiffRapid.Core.Tests;

public sealed class ImageComparerTests
{
    private static readonly PixelColor White = PixelColor.FromRgb(255, 255, 255);

    [Fact]
    public void IdenticalImagesHaveNoDifferences()
    {
        var left = PngCodecTests.RandomImage(20, 10, seed: 1, opaque: true);
        var result = ImageComparer.Compare(left, left.Clone());
        Assert.True(result.AreIdentical);
        Assert.True(result.SameSize);
        Assert.Equal(200, result.SamePixelCount);
        Assert.Equal(0, result.DifferencePercentage);
        Assert.Empty(result.DifferenceRegions);
    }

    [Fact]
    public void CountsDifferingPixelsAndMarksTheirBlock()
    {
        var left = Solid(20, 20, White);
        var right = left.Clone();
        right.SetPixel(10, 10, PixelColor.FromRgb(0, 0, 0));
        var result = ImageComparer.Compare(left, right);
        Assert.Equal(1, result.DifferentPixelCount);
        Assert.Equal(0.25, result.DifferencePercentage);
        Assert.Equal(PixelDiffKind.Different, result.GetKind(10, 10));
        Assert.Equal(PixelDiffKind.Same, result.GetKind(9, 10));
        Assert.Equal([new ImageRegion(8, 8, 8, 8)], result.DifferenceRegions);
    }

    [Fact]
    public void PerChannelToleranceAcceptsSmallChanges()
    {
        var left = Solid(2, 2, PixelColor.FromRgb(100, 100, 100));
        var right = left.Clone();
        right.SetPixel(0, 0, PixelColor.FromRgb(110, 95, 100));
        Assert.Equal(1, ImageComparer.Compare(left, right, new ImageCompareOptions { ColorTolerance = 9 }).DifferenceCount);
        Assert.True(ImageComparer.Compare(left, right, new ImageCompareOptions { ColorTolerance = 10 }).AreIdentical);
    }

    [Fact]
    public void EuclideanToleranceMeasuresColourDistance()
    {
        var left = Solid(1, 1, PixelColor.FromRgb(100, 100, 100));
        var right = Solid(1, 1, PixelColor.FromRgb(103, 104, 100));
        var euclidean = new ImageCompareOptions { ToleranceMode = ColorToleranceMode.Euclidean, ColorTolerance = 4 };
        Assert.False(ImageComparer.Compare(left, right, euclidean).AreIdentical);
        Assert.True(ImageComparer.Compare(left, right, euclidean with { ColorTolerance = 5 }).AreIdentical);
        Assert.True(ImageComparer.Compare(left, right, new ImageCompareOptions { ColorTolerance = 4 }).AreIdentical);
    }

    [Fact]
    public void AlphaCanBeIgnoredAndInvisiblePixelsMatch()
    {
        var left = Solid(2, 1, new PixelColor(10, 20, 30, 255));
        var right = Solid(2, 1, new PixelColor(10, 20, 30, 128));
        Assert.Equal(2, ImageComparer.Compare(left, right).DifferentPixelCount);
        Assert.True(ImageComparer.Compare(left, right, new ImageCompareOptions { IgnoreAlpha = true }).AreIdentical);

        var clearRed = Solid(1, 1, new PixelColor(0, 0, 255, 0));
        var clearBlue = Solid(1, 1, new PixelColor(255, 0, 0, 0));
        Assert.True(ImageComparer.Compare(clearRed, clearBlue).AreIdentical);
        Assert.False(ImageComparer.Compare(clearRed, clearBlue, new ImageCompareOptions { IgnoreAlpha = true }).AreIdentical);
    }

    [Fact]
    public void ComparesDifferentSizesOverTheCommonArea()
    {
        var left = Solid(10, 10, White);
        var right = Solid(12, 8, White);
        var result = ImageComparer.Compare(left, right);
        Assert.False(result.SameSize);
        Assert.Equal((12, 10), (result.Width, result.Height));
        Assert.Equal(80, result.SamePixelCount);
        Assert.Equal(0, result.DifferentPixelCount);
        Assert.Equal(36, result.UnmatchedPixelCount);
        Assert.Equal(116, result.ComparedPixelCount);
        Assert.Equal(PixelDiffKind.LeftOnly, result.GetKind(0, 9));
        Assert.Equal(PixelDiffKind.RightOnly, result.GetKind(11, 0));
        Assert.Equal(PixelDiffKind.Empty, result.GetKind(11, 9));
        Assert.Equal([new ImageRegion(0, 0, 12, 10)], result.DifferenceRegions);
    }

    [Fact]
    public void JoinsTouchingBlocksAndKeepsDistantOnesApart()
    {
        var left = Solid(40, 16, White);
        var right = left.Clone();
        right.SetPixel(0, 0, PixelColor.FromRgb(0, 0, 0));
        right.SetPixel(9, 9, PixelColor.FromRgb(0, 0, 0));
        right.SetPixel(39, 0, PixelColor.FromRgb(0, 0, 0));
        var result = ImageComparer.Compare(left, right);
        Assert.Equal([new ImageRegion(0, 0, 16, 16), new ImageRegion(32, 0, 8, 8)], result.DifferenceRegions);

        var coarse = ImageComparer.Compare(left, right, new ImageCompareOptions { BlockSize = 20 });
        Assert.Equal([new ImageRegion(0, 0, 40, 16)], coarse.DifferenceRegions);
    }

    [Fact]
    public void MergesGroupsWhoseRectanglesOverlap()
    {
        // An L-shaped group around a separate pixel inside its bounding box: one rectangle, not two.
        var left = Solid(6, 4, White);
        var right = left.Clone();
        foreach (var (x, y) in new[] { (0, 0), (0, 1), (0, 2), (1, 2), (2, 2), (3, 2), (2, 0) })
        {
            right.SetPixel(x, y, PixelColor.FromRgb(0, 0, 0));
        }

        var result = ImageComparer.Compare(left, right, new ImageCompareOptions { BlockSize = 1 });
        Assert.Equal([new ImageRegion(0, 0, 4, 3)], result.DifferenceRegions);
    }

    [Fact]
    public void OffsetSearchFindsAShiftedImage()
    {
        var left = PngCodecTests.RandomImage(24, 20, seed: 7, opaque: true);
        var right = new PixelImage(24, 20);
        right.Fill(White);
        for (var y = 0; y < 19; y++)
        {
            for (var x = 0; x < 22; x++)
            {
                right.SetPixel(x, y, left.GetPixel(x + 2, y + 1));
            }
        }

        var plain = ImageComparer.Compare(left, right);
        Assert.True(plain.DifferentPixelCount > 400);

        var shifted = ImageComparer.Compare(left, right, new ImageCompareOptions { MaxOffset = 3 });
        Assert.Equal((2, 1), (shifted.OffsetX, shifted.OffsetY));
        Assert.Equal((0, 0), (shifted.LeftX, shifted.LeftY));
        Assert.Equal((2, 1), (shifted.RightX, shifted.RightY));
        Assert.Equal((26, 21), (shifted.Width, shifted.Height));
        Assert.Equal(22 * 19, shifted.SamePixelCount);
        Assert.Equal(0, shifted.DifferentPixelCount);

        var unchanged = ImageComparer.Compare(left, left.Clone(), new ImageCompareOptions { MaxOffset = 2 });
        Assert.Equal((0, 0), (unchanged.OffsetX, unchanged.OffsetY));
        Assert.True(unchanged.AreIdentical);
    }

    [Fact]
    public void OffsetSearchHandlesNegativeShifts()
    {
        var left = PngCodecTests.RandomImage(16, 16, seed: 8, opaque: true);
        var right = new PixelImage(16, 16);
        right.Fill(White);
        for (var y = 1; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                right.SetPixel(x, y, left.GetPixel(x, y - 1));
            }
        }

        var result = ImageComparer.Compare(left, right, new ImageCompareOptions { MaxOffset = 2 });
        Assert.Equal((0, -1), (result.OffsetX, result.OffsetY));
        Assert.Equal((0, 1), (result.LeftX, result.LeftY));
        Assert.Equal((0, 0), (result.RightX, result.RightY));
        Assert.Equal(0, result.DifferentPixelCount);
    }

    [Fact]
    public void RendersOverlays()
    {
        var left = Solid(2, 1, PixelColor.FromRgb(10, 200, 0));
        var right = Solid(3, 1, PixelColor.FromRgb(13, 100, 0));
        right.SetPixel(0, 0, PixelColor.FromRgb(10, 200, 0));
        var result = ImageComparer.Compare(left, right);

        var xor = ImageOverlay.Xor(left, right, result);
        Assert.Equal((3, 1), (xor.Width, xor.Height));
        Assert.Equal(PixelColor.FromRgb(0, 0, 0), xor.GetPixel(0, 0));
        Assert.Equal(PixelColor.FromRgb(10 ^ 13, 200 ^ 100, 0), xor.GetPixel(1, 0));

        var blend = ImageOverlay.AlphaBlend(left, right, result, 0.5);
        Assert.Equal(PixelColor.FromRgb(12, 150, 0), blend.GetPixel(1, 0));
        Assert.Equal(PixelColor.FromRgb(13, 100, 0), blend.GetPixel(2, 0));
        Assert.Equal(left.GetPixel(1, 0), ImageOverlay.AlphaBlend(left, right, result, 0).GetPixel(1, 0));

        var subtract = ImageOverlay.Subtract(left, right, result, 4);
        Assert.Equal(PixelColor.FromRgb(0, 0, 0), subtract.GetPixel(0, 0));
        Assert.Equal(PixelColor.FromRgb(12, 255, 0), subtract.GetPixel(1, 0));

        var highlight = ImageOverlay.Highlight(left, right, result, dimming: 1);
        Assert.Equal(White, highlight.GetPixel(0, 0));
        Assert.Equal(ImageOverlay.DefaultHighlightColor, highlight.GetPixel(1, 0));
        Assert.Equal(ImageOverlay.DefaultHighlightColor, highlight.GetPixel(2, 0));
        var blue = PixelColor.FromRgb(0, 0, 255);
        Assert.Equal(blue, ImageOverlay.Highlight(left, right, result, blue).GetPixel(2, 0));
        var undimmed = ImageOverlay.Highlight(left, right, result, dimming: 0).GetPixel(0, 0);
        Assert.Equal(PixelColor.FromRgb(120, 120, 120), undimmed);
    }

    [Fact]
    public void ValidatesArguments()
    {
        var image = Solid(2, 2, White);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparer.Compare(image, image, new ImageCompareOptions { ColorTolerance = 256 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparer.Compare(image, image, new ImageCompareOptions { ColorTolerance = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparer.Compare(image, image, new ImageCompareOptions { BlockSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparer.Compare(image, image, new ImageCompareOptions { MaxOffset = -1 }));
        ImageComparer.Compare(image, image, new ImageCompareOptions { ToleranceMode = ColorToleranceMode.Euclidean, ColorTolerance = 510 });

        var result = ImageComparer.Compare(image, image);
        Assert.Throws<ArgumentException>(() => ImageOverlay.Xor(image, Solid(3, 2, White), result));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOverlay.AlphaBlend(image, image, result, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOverlay.Subtract(image, image, result, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetKind(2, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelImage(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelImage(2, 1, 7, new byte[8]));
        Assert.Throws<ArgumentException>(() => new PixelImage(2, 2, 8, new byte[15]));
        Assert.Equal(0, new PixelImage(0, 0).PixelCount);
    }

    [Fact]
    public void ComparesEmptyImages()
    {
        var result = ImageComparer.Compare(new PixelImage(0, 0), Solid(2, 1, White), new ImageCompareOptions { MaxOffset = 2 });
        Assert.Equal(2, result.UnmatchedPixelCount);
        Assert.Equal(100, result.DifferencePercentage);
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        var image = Solid(4, 4, White);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageComparer.Compare(image, image, null, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageComparer.Compare(image, image, new ImageCompareOptions { MaxOffset = 1 }, cancellation.Token));
    }

    [Fact]
    public void CommandLineComparesImagesAndWritesTheHighlight()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var left = Path.Combine(directory, "left.png");
            var right = Path.Combine(directory, "right.png");
            var copy = Path.Combine(directory, "copy.png");
            var diff = Path.Combine(directory, "diff.png");
            var image = Solid(10, 10, PixelColor.FromRgb(100, 100, 100));
            ImageFile.SavePng(image, left);
            ImageFile.SavePng(image, copy);
            var changed = image.Clone();
            changed.SetPixel(3, 4, PixelColor.FromRgb(104, 100, 100));
            ImageFile.SavePng(changed, right);

            Assert.Equal(0, Program.Main(["image", left, copy]));
            Assert.Equal(1, Program.Main(["image", left, right, "--diff-out", diff]));
            Assert.Equal(ImageOverlay.DefaultHighlightColor, ImageFile.Load(diff).GetPixel(3, 4));
            Assert.Equal(0, Program.Main(["image", left, right, "--tolerance", "4"]));
            Assert.Equal(0, Program.Main(["image", left, right, "--tolerance=4", "--max-offset", "1", "--ignore-alpha"]));

            var text = Path.Combine(directory, "notes.txt");
            File.WriteAllText(text, "not an image");
            Assert.Equal(2, Program.Main(["image", left, text]));
            Assert.Equal(2, Program.Main(["image", left, Path.Combine(directory, "missing.png")]));
            Assert.Equal(2, Program.Main(["image", left]));
            Assert.Equal(2, Program.Main(["image", left, right, "--tolerance"]));
            Assert.Equal(2, Program.Main(["image", left, right, "--tolerance", "300"]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static PixelImage Solid(int width, int height, PixelColor color)
    {
        var image = new PixelImage(width, height);
        image.Fill(color);
        return image;
    }
}
