using System.Buffers.Binary;
using System.IO.Compression;
using DiffRapid.Core.Imaging;

namespace DiffRapid.Core.Tests;

/// <summary>Image sizes from headers or offsets are checked before they drive allocations.</summary>
public sealed class ImageLimitsTests
{
    [Fact]
    public void ASmallPngClaimingAHugeSizeFailsWithoutAllocatingIt()
    {
        // 10,000 x 10,000 RGB is within the pixel limit but would need 300 MB of filtered rows.
        var png = BuildPng(10_000, 10_000, [0, 1, 2, 3]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var error = Assert.Throws<InvalidDataException>(() => PngCodec.Decode(png));

        Assert.Contains("shorter", error.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024);
    }

    [Fact]
    public void PngsPastThePixelLimitAreNotSupported()
    {
        var png = BuildPng(10_001, 10_000, [0, 1, 2, 3]);
        Assert.Contains("too large", Assert.Throws<NotSupportedException>(() => PngCodec.Decode(png)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(int.MaxValue, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, int.MaxValue)]
    [InlineData(0, int.MinValue)]
    [InlineData(20_000, 20_000)]
    public void CanvasOffsetsThatOverflowOrPassThePixelLimitAreRejected(int offsetX, int offsetY)
    {
        var image = new PixelImage(1, 1);
        Assert.Throws<ArgumentException>(() => ImageCanvas.Create(image, image, offsetX, offsetY));
    }

    [Fact]
    public void CanvasHoldsBothImagesAtANegativeOffset()
    {
        var canvas = ImageCanvas.Create(new PixelImage(2, 2), new PixelImage(3, 1), -1, 2);
        Assert.Equal((3, 3), (canvas.Width, canvas.Height));
        Assert.Equal((1, 0, 0, 2), (canvas.LeftX, canvas.LeftY, canvas.RightX, canvas.RightY));
    }

    private static byte[] BuildPng(int width, int height, byte[] filtered)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
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
}
