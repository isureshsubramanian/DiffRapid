using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;

namespace DiffRapid.Documents.Internal;

/// <summary>Opens Open XML packages read-only and reports damaged files as <see cref="InvalidDataException"/>.</summary>
internal static class OpenXmlPackages
{
    /// <summary>
    /// The most characters the SDK reads from any one part (an estimate: about 256 MB of XML, far past the
    /// parts of ordinary documents, while capping what one part can cost to parse or hold in memory).
    /// </summary>
    public const long MaxCharactersInPart = 128L * 1024 * 1024;

    /// <summary>
    /// Opens a package from a stream (copying it to memory first when the stream cannot seek) and reads it.
    /// Parts load lazily, so damage found while reading is reported the same way as damage found on opening.
    /// </summary>
    /// <exception cref="InvalidDataException">The package, or a part read from it, is damaged or over a limit.</exception>
    public static TResult Read<TPackage, TResult>(Stream stream, Func<Stream, OpenSettings, TPackage> open, string kind, Func<TPackage, TResult> read)
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
            using var document = open(seekable, new OpenSettings { MaxCharactersInPart = MaxCharactersInPart });
            return read(document);
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
