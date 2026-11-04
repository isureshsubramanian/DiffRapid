using System;
using System.IO;

namespace DiffRapid.Core.Imaging;

/// <summary>Loads and saves images with the built-in PNG and BMP codecs.</summary>
public static class ImageFile
{
    /// <summary>Reads a PNG or BMP file, recognised by its signature rather than its extension.</summary>
    /// <exception cref="InvalidDataException">The file is a damaged PNG or BMP image.</exception>
    /// <exception cref="NotSupportedException">The file is not a PNG or BMP image, or uses a variant the codecs do not read.</exception>
    public static PixelImage Load(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        var data = File.ReadAllBytes(path);
        try
        {
            return Decode(data);
        }
        catch (NotSupportedException ex) when (!PngCodec.HasSignature(data) && !BmpCodec.HasSignature(data))
        {
            throw new NotSupportedException($"'{path}' is not a PNG or BMP image.", ex);
        }
    }

    /// <summary>Decodes PNG or BMP bytes, recognised by their signature.</summary>
    public static PixelImage Decode(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (PngCodec.HasSignature(data))
        {
            return PngCodec.Decode(data);
        }

        if (BmpCodec.HasSignature(data))
        {
            return BmpCodec.Decode(data);
        }

        throw new NotSupportedException("Only PNG and BMP images can be read.");
    }

    /// <summary>Writes an image as a PNG file.</summary>
    public static void SavePng(PixelImage image, string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        // Encode first so a failure does not leave an empty file behind.
        File.WriteAllBytes(path, PngCodec.Encode(image));
    }
}
