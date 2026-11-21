using System.IO;

namespace DiffRapid.Documents.Internal;

/// <summary>Limits that keep a damaged or crafted document from exhausting the stack or memory.</summary>
internal static class ExtractLimits
{
    /// <summary>
    /// The deepest nesting followed (content controls, custom XML, tables in tables, hyperlinks, group shapes).
    /// Real documents nest a few levels; 256 is an estimate that leaves room while keeping recursion well
    /// clear of the end of the stack.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// The most characters of text extracted from one document (an estimate: 128 Mi characters, about
    /// 256 MB as a string, well past the text of real documents).
    /// </summary>
    public const long MaxTextLength = 128L * 1024 * 1024;

    /// <exception cref="InvalidDataException"><paramref name="depth"/> is past <see cref="MaxDepth"/>.</exception>
    public static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"The document is nested more than {MaxDepth} levels deep.");
        }
    }

    /// <summary>Adds <paramref name="count"/> characters to <paramref name="used"/>, checking before adding.</summary>
    /// <exception cref="InvalidDataException">The total would pass <see cref="MaxTextLength"/>.</exception>
    public static void AddText(ref long used, long count)
    {
        if (count > MaxTextLength - used)
        {
            throw new InvalidDataException($"The document's text is longer than {MaxTextLength} characters.");
        }

        used += count;
    }
}
