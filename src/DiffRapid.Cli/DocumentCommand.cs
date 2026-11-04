using System.IO;
using System.Text;
using DiffRapid.Core.Text;
using DiffRapid.Documents;

namespace DiffRapid.Cli;

internal static partial class Program
{
    /// <summary>Loads a Word, Excel or PowerPoint file as its extracted text, and any other file as text.</summary>
    private static TextDocument LoadDocumentOrText(string path, Encoding? encoding)
    {
        string text;
        try
        {
            if (!DocumentText.TryExtract(path, out text))
            {
                return Load(path, encoding);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UsageException($"'{path}': {ex.Message}");
        }

        return TextDocument.Parse(text);
    }
}
