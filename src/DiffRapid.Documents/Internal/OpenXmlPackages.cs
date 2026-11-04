using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;

namespace DiffRapid.Documents.Internal;

/// <summary>Opens Open XML packages read-only and reports damaged files as <see cref="InvalidDataException"/>.</summary>
internal static class OpenXmlPackages
{
    /// <summary>Opens a package from a stream, copying it to memory first when the stream cannot seek.</summary>
    public static TPackage Open<TPackage>(Stream stream, Func<Stream, TPackage> open, string kind)
        where TPackage : OpenXmlPackage
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var seekable = stream;
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            seekable = copy;
        }

        try
        {
            return open(seekable);
        }
        catch (Exception ex) when (IsDamagedPackage(ex))
        {
            throw new InvalidDataException($"The file is not a readable {kind} document: {ex.Message}", ex);
        }
    }

    private static bool IsDamagedPackage(Exception exception) =>
        exception is OpenXmlPackageException
            or InvalidDataException
            or FileFormatException
            or System.Xml.XmlException
            or InvalidOperationException
            or ArgumentException;
}
