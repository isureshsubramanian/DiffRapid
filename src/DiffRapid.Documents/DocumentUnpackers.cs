using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiffRapid.Documents;

/// <summary>A converter that turns one kind of document into comparable text.</summary>
/// <param name="Name">Display name, such as "Word document".</param>
/// <param name="FilePatterns">File name patterns it handles, such as "*.docx".</param>
/// <param name="Extract">Reads the document from a stream and returns its text.</param>
public sealed record DocumentUnpacker(string Name, IReadOnlyList<string> FilePatterns, Func<Stream, string> Extract)
{
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
}

/// <summary>The built-in document converters, for a plugin registry or a compare that picks one by file name.</summary>
public static class DocumentUnpackers
{
    /// <summary>Word, Excel and PowerPoint converters with their default options.</summary>
    public static IReadOnlyList<DocumentUnpacker> All { get; } =
    [
        new DocumentUnpacker("Word document", ["*.docx", "*.docm", "*.dotx", "*.dotm"], stream => DocxText.Extract(stream)),
        new DocumentUnpacker("Excel workbook", ["*.xlsx", "*.xlsm", "*.xltx", "*.xltm"], stream => XlsxText.Extract(stream)),
        new DocumentUnpacker("PowerPoint presentation", ["*.pptx", "*.pptm", "*.potx", "*.potm"], stream => PptxText.Extract(stream)),
    ];

    /// <summary>Returns the converter for a file name, or null when none handles it.</summary>
    public static DocumentUnpacker? Find(string path) => All.FirstOrDefault(unpacker => unpacker.Matches(path));
}
