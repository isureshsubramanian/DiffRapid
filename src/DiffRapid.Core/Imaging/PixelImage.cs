using System;

namespace DiffRapid.Core.Imaging;

/// <summary>One 32-bit pixel, stored in memory as blue, green, red, alpha.</summary>
/// <param name="B">Blue channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="R">Red channel.</param>
/// <param name="A">Alpha channel; 255 is opaque.</param>
public readonly record struct PixelColor(byte B, byte G, byte R, byte A)
{
    /// <summary>Fully transparent black.</summary>
    public static PixelColor Transparent => new(0, 0, 0, 0);

    /// <summary>Creates an opaque colour from red, green and blue.</summary>
    public static PixelColor FromRgb(byte red, byte green, byte blue) => new(blue, green, red, 255);
}

/// <summary>
/// An uncompressed image in BGRA32 layout: four bytes per pixel (blue, green, red, alpha), rows top to bottom,
/// each row starting <see cref="Stride"/> bytes after the previous one. This is the layout WPF's Bgra32 format uses.
/// </summary>
public sealed class PixelImage
{
    /// <summary>Bytes per pixel in the BGRA32 layout.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>Creates a fully transparent image with tightly packed rows.</summary>
    public PixelImage(int width, int height)
        : this(width, height, PackedStride(width), new byte[PackedLength(width, height)])
    {
    }

    /// <summary>Wraps existing BGRA32 pixel bytes without copying them.</summary>
    /// <param name="width">Width in pixels; zero or more.</param>
    /// <param name="height">Height in pixels; zero or more.</param>
    /// <param name="stride">Bytes from the start of one row to the next; at least <c>width * 4</c>.</param>
    /// <param name="pixels">The pixel bytes; long enough for every row.</param>
    public PixelImage(int width, int height, int stride, byte[] pixels)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width cannot be negative.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height cannot be negative.");
        }

        if ((long)stride < (long)width * BytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride must be at least four bytes per pixel of width.");
        }

        if (pixels is null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        if (pixels.LongLength < RequiredLength(width, height, stride))
        {
            throw new ArgumentException("The pixel buffer is too small for the given width, height and stride.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes from the start of one row to the start of the next.</summary>
    public int Stride { get; }

    /// <summary>The BGRA32 pixel bytes.</summary>
    public byte[] Pixels { get; }

    /// <summary>Number of pixels in the image.</summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>Reads the pixel at a position.</summary>
    public PixelColor GetPixel(int x, int y)
    {
        var index = IndexOf(x, y);
        return new PixelColor(Pixels[index], Pixels[index + 1], Pixels[index + 2], Pixels[index + 3]);
    }

    /// <summary>Writes the pixel at a position.</summary>
    public void SetPixel(int x, int y, PixelColor color)
    {
        var index = IndexOf(x, y);
        Pixels[index] = color.B;
        Pixels[index + 1] = color.G;
        Pixels[index + 2] = color.R;
        Pixels[index + 3] = color.A;
    }

    /// <summary>Fills the whole image with one colour.</summary>
    public void Fill(PixelColor color)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                SetPixel(x, y, color);
            }
        }
    }

    /// <summary>Returns a copy with tightly packed rows.</summary>
    public PixelImage Clone()
    {
        var copy = new PixelImage(Width, Height);
        var rowBytes = Width * BytesPerPixel;
        for (var y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(Pixels, y * Stride, copy.Pixels, y * copy.Stride, rowBytes);
        }

        return copy;
    }

    /// <summary>Byte index of a pixel's blue channel.</summary>
    internal int IndexOf(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return y * Stride + x * BytesPerPixel;
    }

    private static int PackedStride(int width)
    {
        if (width < 0 || width > int.MaxValue / BytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be between zero and the largest buffer row.");
        }

        return width * BytesPerPixel;
    }

    private static long PackedLength(int width, int height)
    {
        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height cannot be negative.");
        }

        return RequiredLength(width, height, PackedStride(width));
    }

    private static long RequiredLength(int width, int height, int stride)
    {
        if (width == 0 || height == 0)
        {
            return 0;
        }

        var length = (long)stride * (height - 1) + (long)width * BytesPerPixel;
        if (length > int.MaxValue)
        {
            throw new ArgumentException("The image is too large to hold in one buffer.");
        }

        return length;
    }
}
