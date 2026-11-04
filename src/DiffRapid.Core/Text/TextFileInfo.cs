using System.Text;

namespace DiffRapid.Core.Text;

/// <summary>Encoding and line-ending facts about a decoded file.</summary>
/// <param name="EncodingName">Display name of the encoding used.</param>
/// <param name="HasBom">Whether a matching byte order mark was present.</param>
/// <param name="DominantLineEnding">Most frequent terminator; ties prefer CRLF, then LF, then CR.</param>
/// <param name="IsMixedLineEndings">Whether multiple terminator styles occur.</param>
/// <param name="HasTrailingLineEnding">Whether the final line has a terminator.</param>
/// <param name="LineCount">Line count, zero for empty text, excluding a phantom line after a final terminator.</param>
public sealed record TextFileInfo(string EncodingName, bool HasBom, LineEnding DominantLineEnding,
    bool IsMixedLineEndings, bool HasTrailingLineEnding, int LineCount)
{
    internal static TextFileInfo Create(string text, Encoding encoding, bool hasBom)
    {
        var counts = new int[4];
        var lineCount = 0;
        var trailing = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ending = LineEnding.None;
            if (text[i] == '\r')
            {
                ending = LineEnding.Cr;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    ending = LineEnding.CrLf;
                    i++;
                }
            }
            else if (text[i] == '\n')
            {
                ending = LineEnding.Lf;
            }

            trailing = ending != LineEnding.None;
            if (trailing)
            {
                counts[(int)ending]++;
                lineCount++;
            }
        }

        var dominant = LineEnding.None;
        var maximum = 0;
        var styles = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                styles++;
            }
            if (counts[i] > maximum)
            {
                maximum = counts[i];
                dominant = (LineEnding)i;
            }
        }

        var name = encoding.CodePage switch
        {
            65001 => hasBom ? "UTF-8 with BOM" : "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            12000 => "UTF-32 LE",
            12001 => "UTF-32 BE",
            28591 => "Latin-1",
            1252 => "Windows-1252",
            _ => encoding.WebName,
        };
        return new TextFileInfo(name, hasBom, dominant, styles > 1, trailing,
            lineCount + (text.Length > 0 && !trailing ? 1 : 0));
    }
}
