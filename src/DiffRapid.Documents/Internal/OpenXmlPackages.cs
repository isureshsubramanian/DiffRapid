using System;
using System.IO;
using System.Xml;
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
            CheckPartDepths(document);
            return read(document);
        }
        catch (Exception ex) when (IsDamagedPackage(ex))
        {
            throw new InvalidDataException($"The file is not a readable {kind} document: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Streams every XML part once and rejects any nested deeper than <see cref="ExtractLimits.MaxDepth"/>, before the
    /// SDK builds a DOM of it. The SDK's DOM load recurses once per element, so about 3,000 nested elements overflow
    /// the stack, which no exception handler can catch; the extractors' own depth checks run only after that load.
    /// Every part is checked, including ones the extractors load later (slides, sheets, shared strings, headers, notes).
    /// The cost is one forward-only pass per part, bounded by <see cref="MaxCharactersInPart"/> like the SDK's own read.
    /// </summary>
    /// <exception cref="InvalidDataException">A part nests too deeply.</exception>
    /// <exception cref="XmlException">A part is not well-formed XML, declares a DTD, or is over the character limit.</exception>
    private static void CheckPartDepths(OpenXmlPackage document)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharactersInPart,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        foreach (var part in document.GetAllParts())
        {
            if (part.ContentType.IndexOf("xml", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                ExtractLimits.CheckDepth(reader.Depth);
            }
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
