using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DiffRapid.Documents;

/// <summary>A converter that turns one kind of document into comparable text.</summary>
/// <param name="Name">Display name, such as "Word document".</param>
/// <param name="FilePatterns">File name patterns it handles, such as "*.docx".</param>
/// <param name="Extract">Reads the document from a stream and returns its text.</param>
public sealed record DocumentUnpacker(string Name, IReadOnlyList<string> FilePatterns, Func<Stream, string> Extract)
{
    private readonly Func<Stream, CancellationToken, string>? _cancellableExtract;
    private readonly Func<Stream, string>? _extractWithoutToken;

    /// <summary>Creates a converter whose reader can be cancelled; <see cref="Extract"/> runs it without a token.</summary>
    /// <param name="name">Display name, such as "Word document".</param>
    /// <param name="filePatterns">File name patterns it handles, such as "*.docx".</param>
    /// <param name="extract">Reads the document from a stream and returns its text, stopping when the token is cancelled.</param>
    public DocumentUnpacker(string name, IReadOnlyList<string> filePatterns, Func<Stream, CancellationToken, string> extract)
        : this(name, filePatterns, WithoutToken(extract ?? throw new ArgumentNullException(nameof(extract))))
    {
        _cancellableExtract = extract;
        _extractWithoutToken = Extract;
    }

    /// <summary>Returns whether the file name matches one of the patterns (by extension, ignoring case).</summary>
    public bool Matches(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return FilePatterns.Any(pattern => pattern.Equals("*" + extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the document's text, passing the token to the reader when it accepts one.</summary>
    /// <exception cref="InvalidDataException">The stream is not a readable document.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public string ExtractText(Stream stream, CancellationToken cancellationToken)
    {
        // A copy made with a different Extract (via 'with') must run that reader, not the original one.
        if (_cancellableExtract is not null && ReferenceEquals(Extract, _extractWithoutToken))
        {
            return _cancellableExtract(stream, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Extract(stream);
    }

    private static Func<Stream, string> WithoutToken(Func<Stream, CancellationToken, string> extract) =>
        stream => extract(stream, CancellationToken.None);
}

/// <summary>The built-in document converters, for a plugin registry or a compare that picks one by file name.</summary>
public static class DocumentUnpackers
{
    /// <summary>Word, Excel and PowerPoint converters with their default options.</summary>
    public static IReadOnlyList<DocumentUnpacker> All { get; } =
    [
        new DocumentUnpacker("Word document", ["*.docx", "*.docm", "*.dotx", "*.dotm"], (stream, token) => DocxText.Extract(stream, cancellationToken: token)),
        new DocumentUnpacker("Excel workbook", ["*.xlsx", "*.xlsm", "*.xltx", "*.xltm"], (stream, token) => XlsxText.Extract(stream, cancellationToken: token)),
        new DocumentUnpacker("PowerPoint presentation", ["*.pptx", "*.pptm", "*.potx", "*.potm"], (stream, token) => PptxText.Extract(stream, token)),
    ];

    /// <summary>Returns the converter for a file name, or null when none handles it.</summary>
    public static DocumentUnpacker? Find(string path) => All.FirstOrDefault(unpacker => unpacker.Matches(path));
}
