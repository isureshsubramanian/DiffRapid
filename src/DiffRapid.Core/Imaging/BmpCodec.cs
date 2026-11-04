using System;
using System.IO;

namespace DiffRapid.Core.Imaging;

/// <summary>
/// A small reader for uncompressed 24-bit and 32-bit Windows bitmaps (BI_RGB, or BI_BITFIELDS with 8-bit
/// masks), for the command line and tests. Other bitmaps raise <see cref="NotSupportedException"/>.
/// </summary>
public static class BmpCodec
{
    private const int FileHeaderSize = 14;
    private const int CompressionRgb = 0;
    private const int CompressionBitFields = 3;
    private const int CompressionAlphaBitFields = 6;
    private const string SupportedVariants = "DiffRapid reads uncompressed 24-bit and 32-bit BMP images.";

    /// <summary>True when the bytes start with the bitmap signature "BM".</summary>
    public static bool HasSignature(byte[] data) => data is { Length: >= 2 } && data[0] == (byte)'B' && data[1] == (byte)'M';

    /// <summary>Reads a bitmap from bytes.</summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed bitmap.</exception>
    /// <exception cref="NotSupportedException">The bitmap is compressed, paletted or uses an unusual layout.</exception>
    public static PixelImage Decode(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (!HasSignature(data) || data.Length < FileHeaderSize + 4)
        {
            throw new InvalidDataException("The data is not a BMP image.");
        }

        var header = ReadHeader(data);
        var masks = ReadMasks(data, header);
        var rowBytes = ((long)header.BitCount * header.Width + 31) / 32 * 4;
        if (header.PixelOffset < 0 || header.PixelOffset + rowBytes * header.Height > data.Length)
        {
            throw new InvalidDataException("The BMP image ends before its pixel rows do.");
        }

        if ((long)header.Width * header.Height * PixelImage.BytesPerPixel > int.MaxValue)
        {
            throw new NotSupportedException($"The BMP image is too large ({header.Width} x {header.Height}).");
        }

        return ReadPixels(data, header, masks, (int)rowBytes);
    }

    private static BmpHeader ReadHeader(byte[] data)
    {
        var infoSize = ReadInt32(data, FileHeaderSize);
        if (infoSize != 40 && infoSize != 52 && infoSize != 56 && infoSize != 108 && infoSize != 124)
        {
            throw new NotSupportedException($"BMP header size {infoSize} is not supported. {SupportedVariants}");
        }

        if (data.Length < FileHeaderSize + infoSize)
        {
            throw new InvalidDataException("The BMP header is truncated.");
        }

        var width = ReadInt32(data, FileHeaderSize + 4);
        var height = ReadInt32(data, FileHeaderSize + 8);
        int bitCount = ReadUInt16(data, FileHeaderSize + 14);
        var compression = ReadInt32(data, FileHeaderSize + 16);
        if (width <= 0 || height == 0 || height == int.MinValue)
        {
            throw new InvalidDataException("The BMP image has an invalid size.");
        }

        if (bitCount != 24 && bitCount != 32)
        {
            throw new NotSupportedException($"{bitCount}-bit BMP images are not supported. {SupportedVariants}");
        }

        var isBitFields = compression is CompressionBitFields or CompressionAlphaBitFields;
        if (compression != CompressionRgb && !(isBitFields && bitCount == 32))
        {
            throw new NotSupportedException($"BMP compression {compression} is not supported. {SupportedVariants}");
        }

        return new BmpHeader(ReadInt32(data, 10), infoSize, Math.Abs(height), height > 0, bitCount, compression, width);
    }

    private static ChannelMasks? ReadMasks(byte[] data, BmpHeader header)
    {
        if (header.Compression == CompressionRgb)
        {
            return null;
        }

        // The masks sit inside headers of 52 bytes or more, and straight after a 40-byte header.
        var start = FileHeaderSize + 40;
        var hasAlphaMask = header.InfoSize >= 56 || header.Compression == CompressionAlphaBitFields;
        if (start + (hasAlphaMask ? 16 : 12) > data.Length)
        {
            throw new InvalidDataException("The BMP colour masks are truncated.");
        }

        var masks = new ChannelMasks(
            ReadUInt32(data, start + 8),
            ReadUInt32(data, start + 4),
            ReadUInt32(data, start),
            hasAlphaMask ? ReadUInt32(data, start + 12) : 0);
        masks.Validate();
        return masks;
    }

    private static PixelImage ReadPixels(byte[] data, BmpHeader header, ChannelMasks? masks, int rowBytes)
    {
        var image = new PixelImage(header.Width, header.Height);
        var bytesPerPixel = header.BitCount / 8;
        var anyAlpha = false;
        for (var y = 0; y < header.Height; y++)
        {
            var sourceRow = header.BottomUp ? header.Height - 1 - y : y;
            var source = header.PixelOffset + sourceRow * rowBytes;
            for (var x = 0; x < header.Width; x++)
            {
                var color = masks is { } bitFields
                    ? bitFields.Read(ReadUInt32(data, source))
                    : new PixelColor(data[source], data[source + 1], data[source + 2], bytesPerPixel == 4 ? data[source + 3] : (byte)255);
                anyAlpha |= color.A != 0;
                image.SetPixel(x, y, color);
                source += bytesPerPixel;
            }
        }

        // Most 32-bit bitmaps leave the fourth byte zero rather than storing alpha; treat those as opaque.
        if (!anyAlpha && header.BitCount == 32)
        {
            MakeOpaque(image);
        }

        return image;
    }

    private static void MakeOpaque(PixelImage image)
    {
        for (var index = 3; index < image.Pixels.Length; index += PixelImage.BytesPerPixel)
        {
            image.Pixels[index] = 255;
        }
    }

    private static int ReadInt32(byte[] data, int offset) => (int)ReadUInt32(data, offset);

    private static uint ReadUInt32(byte[] data, int offset) =>
        data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);

    private static int ReadUInt16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

    private sealed record BmpHeader(int PixelOffset, int InfoSize, int Height, bool BottomUp, int BitCount, int Compression, int Width);

    /// <summary>The bit masks of a BI_BITFIELDS bitmap; each must be 8 contiguous bits (or zero for alpha).</summary>
    private readonly record struct ChannelMasks(uint Blue, uint Green, uint Red, uint Alpha)
    {
        public void Validate()
        {
            if (!IsByteMask(Blue) || !IsByteMask(Green) || !IsByteMask(Red) || (Alpha != 0 && !IsByteMask(Alpha)))
            {
                throw new NotSupportedException($"BMP colour masks other than 8 bits per channel are not supported. {SupportedVariants}");
            }
        }

        public PixelColor Read(uint value) => new(
            Extract(value, Blue),
            Extract(value, Green),
            Extract(value, Red),
            Alpha == 0 ? (byte)255 : Extract(value, Alpha));

        private static bool IsByteMask(uint mask) => mask != 0 && (mask >> Shift(mask)) == 0xFF;

        private static byte Extract(uint value, uint mask) => (byte)((value & mask) >> Shift(mask));

        private static int Shift(uint mask)
        {
            var shift = 0;
            while (shift < 32 && ((mask >> shift) & 1) == 0)
            {
                shift++;
            }

            return shift;
        }
    }
}
