using System;

namespace DiffRapid.Core.Imaging;

/// <summary>
/// Renders the two compared images into one image on the comparison canvas. Every renderer takes the images
/// and the <see cref="ImageDiffResult"/> they produced; where only one image covers a pixel, that image's pixel
/// is used as if the other side were transparent black.
/// </summary>
public static class ImageOverlay
{
    /// <summary>Default colour of differing pixels in <see cref="Highlight"/>: opaque red.</summary>
    public static PixelColor DefaultHighlightColor { get; } = PixelColor.FromRgb(255, 0, 0);

    /// <summary>Exclusive-or of the colour channels; matching pixels turn black. Alpha is opaque.</summary>
    public static PixelImage Xor(PixelImage left, PixelImage right, ImageDiffResult result)
    {
        return Render(left, right, result, (leftColor, rightColor, _) => new PixelColor(
            (byte)(leftColor.B ^ rightColor.B),
            (byte)(leftColor.G ^ rightColor.G),
            (byte)(leftColor.R ^ rightColor.R),
            255));
    }

    /// <summary>Blends the right image over the left one.</summary>
    /// <param name="left">The left image.</param>
    /// <param name="right">The right image.</param>
    /// <param name="result">The comparison of the two images.</param>
    /// <param name="amount">0 shows only the left image, 1 only the right one.</param>
    public static PixelImage AlphaBlend(PixelImage left, PixelImage right, ImageDiffResult result, double amount)
    {
        if (double.IsNaN(amount) || amount < 0 || amount > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The blend amount must be between 0 and 1.");
        }

        return Render(left, right, result, (leftColor, rightColor, kind) => kind switch
        {
            PixelDiffKind.LeftOnly => leftColor,
            PixelDiffKind.RightOnly => rightColor,
            _ => new PixelColor(
                Mix(leftColor.B, rightColor.B, amount),
                Mix(leftColor.G, rightColor.G, amount),
                Mix(leftColor.R, rightColor.R, amount),
                Mix(leftColor.A, rightColor.A, amount)),
        });
    }

    /// <summary>
    /// A faded greyscale copy of the left image (the right image where the left is missing) with every
    /// differing pixel painted in <paramref name="highlightColor"/>.
    /// </summary>
    /// <param name="left">The left image.</param>
    /// <param name="right">The right image.</param>
    /// <param name="result">The comparison of the two images.</param>
    /// <param name="highlightColor">Colour of differing pixels; <see cref="DefaultHighlightColor"/> when null.</param>
    /// <param name="dimming">How far matching pixels fade towards white, 0 (not at all) to 1 (white).</param>
    public static PixelImage Highlight(PixelImage left, PixelImage right, ImageDiffResult result, PixelColor? highlightColor = null, double dimming = 0.6)
    {
        if (double.IsNaN(dimming) || dimming < 0 || dimming > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dimming), "The dimming must be between 0 and 1.");
        }

        var highlight = highlightColor ?? DefaultHighlightColor;
        return Render(left, right, result, (leftColor, rightColor, kind) =>
        {
            if (ImageDiffResult.IsDifference(kind))
            {
                return highlight;
            }

            var source = kind == PixelDiffKind.Same ? leftColor : rightColor;
            var grey = (byte)((source.R * 299 + source.G * 587 + source.B * 114) / 1000);
            var faded = Mix(grey, 255, dimming);
            return new PixelColor(faded, faded, faded, kind == PixelDiffKind.Empty ? (byte)0 : (byte)255);
        });
    }

    /// <summary>The absolute difference of each colour channel, multiplied by <paramref name="amplification"/>.</summary>
    /// <param name="left">The left image.</param>
    /// <param name="right">The right image.</param>
    /// <param name="result">The comparison of the two images.</param>
    /// <param name="amplification">Factor applied to each difference so small changes show; 1 or more.</param>
    public static PixelImage Subtract(PixelImage left, PixelImage right, ImageDiffResult result, int amplification = 4)
    {
        if (amplification < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amplification), "The amplification must be at least 1.");
        }

        return Render(left, right, result, (leftColor, rightColor, _) => new PixelColor(
            Amplify(leftColor.B, rightColor.B, amplification),
            Amplify(leftColor.G, rightColor.G, amplification),
            Amplify(leftColor.R, rightColor.R, amplification),
            255));
    }

    private static PixelImage Render(PixelImage left, PixelImage right, ImageDiffResult result, Func<PixelColor, PixelColor, PixelDiffKind, PixelColor> combine)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        result.CheckImages(left, right);
        var output = new PixelImage(result.Width, result.Height);
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var kind = result.KindAt((long)y * result.Width + x);
                var leftColor = ReadOrTransparent(left, x - result.LeftX, y - result.LeftY);
                var rightColor = ReadOrTransparent(right, x - result.RightX, y - result.RightY);
                output.SetPixel(x, y, combine(leftColor, rightColor, kind));
            }
        }

        return output;
    }

    private static PixelColor ReadOrTransparent(PixelImage image, int x, int y)
    {
        var inside = (uint)x < (uint)image.Width && (uint)y < (uint)image.Height;
        return inside ? image.GetPixel(x, y) : PixelColor.Transparent;
    }

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);

    private static byte Amplify(byte leftValue, byte rightValue, int amplification) => (byte)Math.Min(255L, (long)Math.Abs(leftValue - rightValue) * amplification);
}
