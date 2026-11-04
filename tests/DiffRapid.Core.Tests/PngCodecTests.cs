using System.Buffers.Binary;
using System.IO.Compression;
using DiffRapid.Core.Imaging;

namespace DiffRapid.Core.Tests;

public sealed class PngCodecTests
{
    [Fact]
    public void RoundTripsTranslucentImageAsRgba()
    {
        var image = RandomImage(17, 9, seed: 1, opaque: false);
        var bytes = PngCodec.Encode(image);
        Assert.Equal(6, bytes[25]);
        AssertSamePixels(image, PngCodec.Decode(bytes));
    }

    [Fact]
    public void RoundTripsOpaqueImageAsRgb()
    {
        var image = RandomImage(5, 31, seed: 2, opaque: true);
        var bytes = PngCodec.Encode(image);
        Assert.Equal(2, bytes[25]);
        AssertSamePixels(image, PngCodec.Decode(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RoundTripsEveryRowFilter(int filter)
    {
        foreach (var opaque in new[] { true, false })
        {
            var image = RandomImage(13, 7, seed: 10 + filter, opaque);
            using var stream = new MemoryStream();
            PngCodec.Encode(image, stream, (PngCodec.FilterType)filter);
            var bytes = stream.ToArray();
            AssertSamePixels(image, PngCodec.Decode(bytes));
        }
    }

    [Fact]
    public void EncodesImagesWithPaddedRows()
    {
        var pixels = new byte[3 * 40];
        var image = new PixelImage(2, 3, 40, pixels);
        image.SetPixel(1, 2, new PixelColor(1, 2, 3, 4));
        image.SetPixel(0, 1, new PixelColor(9, 8, 7, 6));
        AssertSamePixels(image, PngCodec.Decode(PngCodec.Encode(image)));
    }

    [Fact]
    public void DecodesHandFilteredRowsOfEveryKind()
    {
        // Two RGB pixels per row; rows use Sub, Up, Average, Paeth and None in turn.
        byte[] filtered =
        [
            1, 10, 20, 30, 5, 5, 5,
            2, 2, 2, 2, 5, 5, 5,
            3, 8, 13, 18, 13, 13, 13,
            4, 36, 2, 2, 235, 1, 254,
            0, 1, 2, 3, 4, 5, 6,
        ];
        var image = PngCodec.Decode(BuildPng(2, 5, 2, 8, 0, filtered));
        Assert.Equal(PixelColor.FromRgb(10, 20, 30), image.GetPixel(0, 0));
        Assert.Equal(PixelColor.FromRgb(15, 25, 35), image.GetPixel(1, 0));
        Assert.Equal(PixelColor.FromRgb(12, 22, 32), image.GetPixel(0, 1));
        Assert.Equal(PixelColor.FromRgb(20, 30, 40), image.GetPixel(1, 1));
        Assert.Equal(PixelColor.FromRgb(14, 24, 34), image.GetPixel(0, 2));
        Assert.Equal(PixelColor.FromRgb(30, 40, 50), image.GetPixel(1, 2));
        Assert.Equal(PixelColor.FromRgb(50, 26, 36), image.GetPixel(0, 3));
        Assert.Equal(PixelColor.FromRgb(29, 41, 48), image.GetPixel(1, 3));
        Assert.Equal(PixelColor.FromRgb(4, 5, 6), image.GetPixel(1, 4));
    }

    [Fact]
    public void DecodesRgbaAlpha()
    {
        var image = PngCodec.Decode(BuildPng(1, 1, 6, 8, 0, [0, 1, 2, 3, 128]));
        Assert.Equal(new PixelColor(3, 2, 1, 128), image.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(3, 8, 0, "colour type 3")]
    [InlineData(0, 8, 0, "colour type 0")]
    [InlineData(4, 8, 0, "colour type 4")]
    [InlineData(2, 16, 0, "bit depth 16")]
    [InlineData(6, 16, 0, "bit depth 16")]
    [InlineData(2, 8, 1, "Interlaced")]
    public void RejectsUnsupportedVariantsClearly(byte colorType, byte bitDepth, byte interlace, string expected)
    {
        var png = BuildPng(1, 1, colorType, bitDepth, interlace, [0, 0, 0, 0, 0, 0, 0]);
        var error = Assert.Throws<NotSupportedException>(() => PngCodec.Decode(png));
        Assert.Contains(expected, error.Message);
        Assert.Contains("8-bit RGB and RGBA", error.Message);
    }

    [Fact]
    public void RejectsDamagedFiles()
    {
        var valid = PngCodec.Encode(RandomImage(4, 4, seed: 3, opaque: true));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(valid[..^20]));

        var badCrc = (byte[])valid.Clone();
        badCrc[20] ^= 0xFF;
        Assert.Contains("CRC", Assert.Throws<InvalidDataException>(() => PngCodec.Decode(badCrc)).Message);

        var badFilter = BuildPng(1, 1, 2, 8, 0, [9, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(badFilter));

        var shortData = BuildPng(2, 2, 2, 8, 0, [0, 1, 2, 3, 4, 5, 6]);
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(shortData));
    }

    [Fact]
    public void RejectsEmptyImagesWhenEncoding()
    {
        Assert.Throws<ArgumentException>(() => PngCodec.Encode(new PixelImage(0, 3)));
    }

    [Fact]
    public void ReadsUncompressedBitmaps()
    {
        // 24-bit, bottom-up, 3 pixels per row so each row needs one padding byte.
        byte[][] rows = [[1, 2, 3, 4, 5, 6, 7, 8, 9], [10, 11, 12, 13, 14, 15, 16, 17, 18]];
        var image = BmpCodec.Decode(BuildBmp(3, 2, 24, 0, rows));
        Assert.Equal(new PixelColor(1, 2, 3, 255), image.GetPixel(0, 0));
        Assert.Equal(new PixelColor(16, 17, 18, 255), image.GetPixel(2, 1));

        // 32-bit, top-down, with the fourth byte unused (zero), which means opaque.
        byte[][] opaqueRows = [[1, 2, 3, 0, 4, 5, 6, 0]];
        var opaque = BmpCodec.Decode(BuildBmp(2, -1, 32, 0, opaqueRows));
        Assert.Equal(new PixelColor(4, 5, 6, 255), opaque.GetPixel(1, 0));

        byte[][] alphaRows = [[1, 2, 3, 0, 4, 5, 6, 77]];
        var withAlpha = BmpCodec.Decode(BuildBmp(2, -1, 32, 0, alphaRows));
        Assert.Equal(new PixelColor(1, 2, 3, 0), withAlpha.GetPixel(0, 0));
        Assert.Equal(new PixelColor(4, 5, 6, 77), withAlpha.GetPixel(1, 0));
    }

    [Fact]
    public void ReadsBitFieldBitmaps()
    {
        // Masks ordered red, green, blue: a pixel stored as 0xAARRGGBB-style RGBA in reverse.
        byte[][] rows = [[0x11, 0x22, 0x33, 0x44]];
        var masks = new uint[] { 0x000000FF, 0x0000FF00, 0x00FF0000 };
        var image = BmpCodec.Decode(BuildBmp(1, 1, 32, 3, rows, masks));
        Assert.Equal(PixelColor.FromRgb(0x11, 0x22, 0x33), image.GetPixel(0, 0));

        var oddMasks = new uint[] { 0x0000F800, 0x000007E0, 0x0000001F };
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(BuildBmp(1, 1, 32, 3, rows, oddMasks)));
    }

    [Fact]
    public void RejectsOtherBitmaps()
    {
        byte[][] rows = [[1, 0, 0, 0]];
        Assert.Contains("8-bit", Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(BuildBmp(1, 1, 8, 0, rows))).Message);
        Assert.Throws<NotSupportedException>(() => BmpCodec.Decode(BuildBmp(1, 1, 24, 1, rows)));
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode(BuildBmp(4, 4, 24, 0, rows)));
        Assert.Throws<InvalidDataException>(() => BmpCodec.Decode([(byte)'B', (byte)'M', 0]));
    }

    [Fact]
    public void ImageFileRecognisesFormatsBySignature()
    {
        var png = PngCodec.Encode(RandomImage(2, 2, seed: 4, opaque: true));
        byte[][] rows = [[1, 2, 3, 0]];
        Assert.Equal(2, ImageFile.Decode(png).Width);
        Assert.Equal(1, ImageFile.Decode(BuildBmp(1, 1, 24, 0, rows)).Width);
        Assert.Throws<NotSupportedException>(() => ImageFile.Decode([(byte)'G', (byte)'I', (byte)'F', (byte)'8']));

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "image.bin");
            var image = RandomImage(3, 3, seed: 5, opaque: false);
            ImageFile.SavePng(image, path);
            AssertSamePixels(image, ImageFile.Load(path));

            var text = Path.Combine(directory, "notes.png");
            File.WriteAllText(text, "not an image");
            Assert.Contains("notes.png", Assert.Throws<NotSupportedException>(() => ImageFile.Load(text)).Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    internal static PixelImage RandomImage(int width, int height, int seed, bool opaque)
    {
        var random = new Random(seed);
        var image = new PixelImage(width, height);
        random.NextBytes(image.Pixels);
        if (opaque)
        {
            for (var index = 3; index < image.Pixels.Length; index += 4)
            {
                image.Pixels[index] = 255;
            }
        }

        return image;
    }

    internal static void AssertSamePixels(PixelImage expected, PixelImage actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    private static byte[] BuildPng(int width, int height, byte colorType, byte bitDepth, byte interlace, byte[] filtered)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[12] = interlace;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(filtered);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Checksums.UpdateCrc32(0xFFFFFFFF, typed, 0, typed.Length) ^ 0xFFFFFFFF);
        output.Write(lengthBytes);
        output.Write(typed);
        output.Write(crcBytes);
    }

    private static byte[] BuildBmp(int width, int height, int bitCount, int compression, byte[][] rows, uint[]? masks = null)
    {
        var maskBytes = masks?.Length * 4 ?? 0;
        var pixelOffset = 14 + 40 + maskBytes;
        var rowBytes = (bitCount * width + 31) / 32 * 4;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(pixelOffset + rowBytes * rows.Length);
        writer.Write(0);
        writer.Write(pixelOffset);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)bitCount);
        writer.Write(compression);
        writer.Write(rowBytes * rows.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        foreach (var mask in masks ?? [])
        {
            writer.Write(mask);
        }

        // Rows are given top to bottom; a positive height stores them bottom-up.
        var stored = height > 0 ? Enumerable.Reverse(rows) : rows;
        foreach (var row in stored)
        {
            writer.Write(row);
            writer.Write(new byte[Math.Max(0, rowBytes - row.Length)]);
        }

        writer.Flush();
        return output.ToArray();
    }
}
