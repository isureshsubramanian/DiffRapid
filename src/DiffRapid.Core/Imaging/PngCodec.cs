using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DiffRapid.Core.Imaging;

/// <summary>
/// A small PNG reader and writer for the command line and tests. It reads 8-bit RGB and RGBA images without
/// interlacing (colour types 2 and 6); every other variant raises <see cref="NotSupportedException"/>. It writes
/// RGBA, or RGB when every pixel is opaque. The Visual Studio extension decodes images with WPF instead.
/// </summary>
public static class PngCodec
{
    private const int ColorTypeRgb = 2;
    private const int ColorTypeRgba = 6;
    private const string SupportedVariants = "DiffRapid reads 8-bit RGB and RGBA PNG images without interlacing.";

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>The PNG row filters.</summary>
    internal enum FilterType : byte
    {
        None = 0,
        Sub = 1,
        Up = 2,
        Average = 3,
        Paeth = 4,
    }

    /// <summary>True when the bytes start with the PNG signature.</summary>
    public static bool HasSignature(byte[] data)
    {
        if (data is null || data.Length < Signature.Length)
        {
            return false;
        }

        for (var index = 0; index < Signature.Length; index++)
        {
            if (data[index] != Signature[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a PNG image from a stream.</summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed PNG file.</exception>
    /// <exception cref="NotSupportedException">The PNG uses a colour type, bit depth or interlacing this reader does not handle.</exception>
    public static PixelImage Decode(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    /// <summary>Reads a PNG image from bytes.</summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed PNG file.</exception>
    /// <exception cref="NotSupportedException">The PNG uses a colour type, bit depth or interlacing this reader does not handle.</exception>
    public static PixelImage Decode(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (!HasSignature(data))
        {
            throw new InvalidDataException("The data is not a PNG image.");
        }

        var (header, compressed) = ReadChunks(data);
        var channels = header.ColorType == ColorTypeRgba ? 4 : 3;
        var rowBytes = (long)header.Width * channels;
        var filteredLength = (rowBytes + 1) * header.Height;
        if (filteredLength > int.MaxValue || (long)header.Width * header.Height * PixelImage.BytesPerPixel > int.MaxValue)
        {
            throw new NotSupportedException($"The PNG image is too large ({header.Width} x {header.Height}).");
        }

        var filtered = Inflate(compressed, (int)filteredLength);
        var raw = Unfilter(filtered, (int)rowBytes, header.Height, channels);
        return ToPixelImage(raw, header.Width, header.Height, channels);
    }

    /// <summary>Writes an image as PNG.</summary>
    public static void Encode(PixelImage image, Stream output)
    {
        Encode(image, output, null);
    }

    /// <summary>Writes an image as PNG and returns the bytes.</summary>
    public static byte[] Encode(PixelImage image)
    {
        using var buffer = new MemoryStream();
        Encode(image, buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes an image as PNG, with every row using <paramref name="forcedFilter"/> when given.</summary>
    internal static void Encode(PixelImage image, Stream output, FilterType? forcedFilter)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        if (image.Width == 0 || image.Height == 0)
        {
            throw new ArgumentException("A PNG image must be at least one pixel wide and high.", nameof(image));
        }

        var channels = IsOpaque(image) ? 3 : 4;
        output.Write(Signature, 0, Signature.Length);
        WriteChunk(output, "IHDR", BuildHeader(image, channels == 4 ? ColorTypeRgba : ColorTypeRgb));
        WriteChunk(output, "IDAT", Deflate(FilterRows(image, channels, forcedFilter)));
        WriteChunk(output, "IEND", []);
    }

    private static (PngHeader Header, byte[] Compressed) ReadChunks(byte[] data)
    {
        PngHeader? header = null;
        using var compressed = new MemoryStream();
        var position = Signature.Length;
        while (true)
        {
            if (position + 12 > data.Length)
            {
                throw new InvalidDataException("The PNG image ends before its IEND chunk.");
            }

            var length = ReadUInt32(data, position);
            if (length > (uint)(data.Length - position - 12))
            {
                throw new InvalidDataException("A PNG chunk runs past the end of the file.");
            }

            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            var dataStart = position + 8;
            CheckCrc(data, position + 4, (int)length + 4, ReadUInt32(data, dataStart + (int)length), type);
            if (header is null && type != "IHDR")
            {
                throw new InvalidDataException("The PNG image does not start with an IHDR chunk.");
            }

            switch (type)
            {
                case "IHDR":
                    header = ReadHeader(data, dataStart, (int)length);
                    break;
                case "IDAT":
                    compressed.Write(data, dataStart, (int)length);
                    break;
                case "IEND":
                    if (compressed.Length == 0)
                    {
                        throw new InvalidDataException("The PNG image has no IDAT chunk.");
                    }

                    return (header!, compressed.ToArray());
            }

            position = dataStart + (int)length + 4;
        }
    }

    private static PngHeader ReadHeader(byte[] data, int start, int length)
    {
        if (length != 13)
        {
            throw new InvalidDataException("The PNG IHDR chunk has the wrong length.");
        }

        var width = ReadUInt32(data, start);
        var height = ReadUInt32(data, start + 4);
        int bitDepth = data[start + 8];
        int colorType = data[start + 9];
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("The PNG image has an invalid size.");
        }

        if (data[start + 10] != 0 || data[start + 11] != 0)
        {
            throw new InvalidDataException("The PNG image uses an unknown compression or filter method.");
        }

        if (colorType != ColorTypeRgb && colorType != ColorTypeRgba)
        {
            throw new NotSupportedException($"PNG colour type {colorType} ({DescribeColorType(colorType)}) is not supported. {SupportedVariants}");
        }

        if (bitDepth != 8)
        {
            throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported. {SupportedVariants}");
        }

        if (data[start + 12] != 0)
        {
            throw new NotSupportedException($"Interlaced PNG images are not supported. {SupportedVariants}");
        }

        return new PngHeader((int)width, (int)height, colorType);
    }

    private static string DescribeColorType(int colorType) => colorType switch
    {
        0 => "greyscale",
        3 => "palette",
        4 => "greyscale with alpha",
        _ => "unknown",
    };

    private static void CheckCrc(byte[] data, int start, int count, uint expected, string type)
    {
        var actual = Checksums.UpdateCrc32(0xFFFFFFFF, data, start, count) ^ 0xFFFFFFFF;
        if (actual != expected)
        {
            throw new InvalidDataException($"The PNG chunk '{type}' is corrupt (CRC mismatch).");
        }
    }

    private static byte[] Inflate(byte[] zlib, int expectedLength)
    {
        if (zlib.Length < 6)
        {
            throw new InvalidDataException("The PNG image data is too short.");
        }

        var method = zlib[0];
        var flags = zlib[1];
        var headerIsValid = (method & 0x0F) == 8 && ((method << 8) | flags) % 31 == 0 && (flags & 0x20) == 0;
        if (!headerIsValid)
        {
            throw new InvalidDataException("The PNG image data does not have a valid zlib header.");
        }

        // DeflateStream reports corrupt data as InvalidDataException, like the rest of this reader.
        var output = new byte[expectedLength];
        using (var source = new MemoryStream(zlib, 2, zlib.Length - 2))
        using (var inflater = new DeflateStream(source, CompressionMode.Decompress))
        {
            ReadExactly(inflater, output);
        }

        if (Checksums.Adler32(output, 0, output.Length) != ReadUInt32(zlib, zlib.Length - 4))
        {
            throw new InvalidDataException("The PNG image data is corrupt (Adler-32 mismatch).");
        }

        return output;
    }

    private static void ReadExactly(Stream stream, byte[] output)
    {
        var filled = 0;
        while (filled < output.Length)
        {
            var read = stream.Read(output, filled, output.Length - filled);
            if (read == 0)
            {
                throw new InvalidDataException("The PNG image data is shorter than its size says.");
            }

            filled += read;
        }
    }

    private static byte[] Unfilter(byte[] filtered, int rowBytes, int height, int channels)
    {
        var raw = new byte[(long)rowBytes * height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var row = 0; row < height; row++)
        {
            var start = row * (rowBytes + 1);
            var filter = filtered[start];
            if (filter > (byte)FilterType.Paeth)
            {
                throw new InvalidDataException($"The PNG image uses unknown row filter {filter}.");
            }

            Buffer.BlockCopy(filtered, start + 1, current, 0, rowBytes);
            UnfilterRow((FilterType)filter, current, previous, channels);
            Buffer.BlockCopy(current, 0, raw, row * rowBytes, rowBytes);
            (previous, current) = (current, previous);
        }

        return raw;
    }

    private static void UnfilterRow(FilterType filter, byte[] row, byte[] previous, int channels)
    {
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= channels ? row[index - channels] : 0;
            var up = previous[index];
            var upLeft = index >= channels ? previous[index - channels] : 0;
            row[index] = (byte)(row[index] + Predict(filter, left, up, upLeft));
        }
    }

    private static int Predict(FilterType filter, int left, int up, int upLeft) => filter switch
    {
        FilterType.Sub => left,
        FilterType.Up => up,
        FilterType.Average => (left + up) / 2,
        FilterType.Paeth => Paeth(left, up, upLeft),
        _ => 0,
    };

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        if (toLeft <= toUp && toLeft <= toUpLeft)
        {
            return left;
        }

        return toUp <= toUpLeft ? up : upLeft;
    }

    private static PixelImage ToPixelImage(byte[] raw, int width, int height, int channels)
    {
        var image = new PixelImage(width, height);
        var pixels = image.Pixels;
        var source = 0;
        for (var target = 0; target < pixels.Length; target += PixelImage.BytesPerPixel)
        {
            pixels[target] = raw[source + 2];
            pixels[target + 1] = raw[source + 1];
            pixels[target + 2] = raw[source];
            pixels[target + 3] = channels == 4 ? raw[source + 3] : (byte)255;
            source += channels;
        }

        return image;
    }

    private static bool IsOpaque(PixelImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[image.IndexOf(x, y) + 3] != 255)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static byte[] BuildHeader(PixelImage image, int colorType)
    {
        var header = new byte[13];
        WriteUInt32(header, 0, (uint)image.Width);
        WriteUInt32(header, 4, (uint)image.Height);
        header[8] = 8;
        header[9] = (byte)colorType;
        return header;
    }

    /// <summary>Converts each row to RGB(A) and filters it, choosing the filter with the smallest output unless one is forced.</summary>
    private static byte[] FilterRows(PixelImage image, int channels, FilterType? forcedFilter)
    {
        var rowBytes = image.Width * channels;
        var output = new byte[(long)(rowBytes + 1) * image.Height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        for (var y = 0; y < image.Height; y++)
        {
            ReadRow(image, y, channels, current);
            var chosen = forcedFilter ?? FilterType.None;
            var bestCost = long.MaxValue;
            for (var filter = FilterType.None; filter <= FilterType.Paeth; filter++)
            {
                if (forcedFilter is { } forced && filter != forced)
                {
                    continue;
                }

                var cost = FilterRow(filter, current, previous, channels, candidate);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    chosen = filter;
                    (best, candidate) = (candidate, best);
                }
            }

            var start = y * (rowBytes + 1);
            output[start] = (byte)chosen;
            Buffer.BlockCopy(best, 0, output, start + 1, rowBytes);
            (previous, current) = (current, previous);
        }

        return output;
    }

    private static void ReadRow(PixelImage image, int y, int channels, byte[] row)
    {
        var target = 0;
        for (var x = 0; x < image.Width; x++)
        {
            var index = image.IndexOf(x, y);
            row[target] = image.Pixels[index + 2];
            row[target + 1] = image.Pixels[index + 1];
            row[target + 2] = image.Pixels[index];
            if (channels == 4)
            {
                row[target + 3] = image.Pixels[index + 3];
            }

            target += channels;
        }
    }

    /// <summary>Filters one row into <paramref name="output"/> and returns the sum of the bytes read as signed values.</summary>
    private static long FilterRow(FilterType filter, byte[] row, byte[] previous, int channels, byte[] output)
    {
        long cost = 0;
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= channels ? row[index - channels] : 0;
            var upLeft = index >= channels ? previous[index - channels] : 0;
            var value = (byte)(row[index] - Predict(filter, left, previous[index], upLeft));
            output[index] = value;
            cost += Math.Abs((int)(sbyte)value);
        }

        return cost;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflater = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(data, 0, data.Length);
        }

        var checksum = new byte[4];
        WriteUInt32(checksum, 0, Checksums.Adler32(data, 0, data.Length));
        output.Write(checksum, 0, checksum.Length);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var header = new byte[8];
        WriteUInt32(header, 0, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, header, 4);
        var crc = Checksums.UpdateCrc32(0xFFFFFFFF, header, 4, 4);
        crc = Checksums.UpdateCrc32(crc, data, 0, data.Length) ^ 0xFFFFFFFF;
        var trailer = new byte[4];
        WriteUInt32(trailer, 0, crc);
        output.Write(header, 0, header.Length);
        output.Write(data, 0, data.Length);
        output.Write(trailer, 0, trailer.Length);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed record PngHeader(int Width, int Height, int ColorType);
}
