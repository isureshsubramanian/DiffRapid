using System.IO;

namespace DiffRapid.Documents;

/// <summary>Reads Office documents as text, chosen by file extension.</summary>
public static class DocumentText
{
    /// <summary>
    /// Extracts the text of a Word, Excel or PowerPoint file. Returns false (and an empty string) when the
    /// extension is not one of <see cref="DocumentUnpackers.All"/>, so the caller reads the file as usual.
    /// </summary>
    /// <exception cref="InvalidDataException">The extension matches but the file is not a readable document.</exception>
    public static bool TryExtract(string path, out string text)
    {
        var unpacker = DocumentUnpackers.Find(path);
        if (unpacker is null)
        {
            text = string.Empty;
            return false;
        }

        using var stream = File.OpenRead(path);
        text = unpacker.Extract(stream);
        return true;
    }
}
