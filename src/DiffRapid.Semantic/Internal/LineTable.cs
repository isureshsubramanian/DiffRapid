using System;
using System.Collections.Generic;

namespace DiffRapid.Semantic.Internal;

/// <summary>
/// Line starts of a text, split on CRLF, LF and CR exactly as <c>TextDocument.Parse</c> does, so that line
/// numbers here and in a Core comparison of the same text agree.
/// </summary>
internal sealed class LineTable
{
    private readonly int[] _starts;
    private readonly int[] _contentEnds;

    private LineTable(int[] starts, int[] contentEnds)
    {
        _starts = starts;
        _contentEnds = contentEnds;
    }

    public int Count => _starts.Length;

    public static LineTable Create(string text)
    {
        var starts = new List<int>();
        var contentEnds = new List<int>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r' || c == '\n')
            {
                starts.Add(start);
                contentEnds.Add(i);
                var crlf = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n';
                i += crlf ? 2 : 1;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length)
        {
            starts.Add(start);
            contentEnds.Add(text.Length);
        }

        return new LineTable(starts.ToArray(), contentEnds.ToArray());
    }

    /// <summary>Start position of a line.</summary>
    public int Start(int line) => _starts[line];

    /// <summary>End of a line's content, before its terminator.</summary>
    public int ContentEnd(int line) => _contentEnds[line];

    /// <summary>The line holding a position; positions past the end belong to the last line.</summary>
    public int LineOf(int position)
    {
        if (_starts.Length == 0)
        {
            return 0;
        }

        var index = Array.BinarySearch(_starts, position);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Max(0, Math.Min(index, _starts.Length - 1));
    }

    /// <summary>Splits [start, end) into per-line pieces that exclude line terminators.</summary>
    public IEnumerable<(int Line, int Start, int End)> Segments(int start, int end)
    {
        if (end <= start || _starts.Length == 0)
        {
            yield break;
        }

        for (var line = LineOf(start); line < _starts.Length && _starts[line] < end; line++)
        {
            var segmentStart = Math.Max(start, _starts[line]);
            var segmentEnd = Math.Min(end, _contentEnds[line]);
            if (segmentEnd > segmentStart)
            {
                yield return (line, segmentStart, segmentEnd);
            }
        }
    }
}
