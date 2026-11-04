using System;
using System.Collections.Generic;
using System.Text;

namespace DiffRapid.Core.Text;

/// <summary>An immutable sequence of lines that preserves the original line endings.</summary>
public sealed class TextDocument
{
    public static readonly TextDocument Empty = new(Array.Empty<TextLine>());

    private readonly TextLine[] _lines;

    public TextDocument(IReadOnlyList<TextLine> lines)
    {
        _lines = new TextLine[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            _lines[i] = lines[i];
        }
    }

    public IReadOnlyList<TextLine> Lines => _lines;

    public int LineCount => _lines.Length;

    public TextLine this[int index] => _lines[index];

    /// <summary>True when the last line has no terminator (WinMerge: "missing trailing EOL").</summary>
    public bool MissingFinalLineEnding => _lines.Length > 0 && _lines[_lines.Length - 1].Ending == LineEnding.None;

    /// <summary>Splits text on CRLF, LF and CR, keeping each line's terminator.</summary>
    public static TextDocument Parse(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var lines = new List<TextLine>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r')
            {
                var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                lines.Add(new TextLine(text.Substring(start, i - start), crlf ? LineEnding.CrLf : LineEnding.Cr));
                i += crlf ? 2 : 1;
                start = i;
            }
            else if (c == '\n')
            {
                lines.Add(new TextLine(text.Substring(start, i - start), LineEnding.Lf));
                i++;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length)
        {
            lines.Add(new TextLine(text.Substring(start), LineEnding.None));
        }

        return new TextDocument(lines);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            sb.Append(line.Content).Append(line.EndingText);
        }

        return sb.ToString();
    }
}
