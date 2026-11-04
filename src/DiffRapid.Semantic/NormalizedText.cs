using System;
using System.Collections.Generic;
using DiffRapid.Core.Text;

namespace DiffRapid.Semantic;

/// <summary>C# differences that can be ignored when comparing source files.</summary>
[Flags]
public enum CSharpIgnoreOptions
{
    None = 0,

    /// <summary>The order of using directives within one block of usings.</summary>
    UsingOrder = 1,

    /// <summary>Whitespace, indentation, blank lines and line breaks between tokens.</summary>
    Formatting = 2,

    /// <summary>Line, block and documentation comments.</summary>
    Comments = 4,

    /// <summary>#region and #endregion lines.</summary>
    Regions = 8,

    All = UsingOrder | Formatting | Comments | Regions,
}

/// <summary>
/// C# source after the selected ignore rules were applied, one entry per remaining line, with the original
/// line each one came from.
/// </summary>
public sealed class NormalizedText
{
    private readonly string[] _lines;
    private readonly int[] _originalLines;
    private int[]? _normalizedLines;

    internal NormalizedText(string[] lines, int[] originalLines, int originalLineCount)
    {
        _lines = lines;
        _originalLines = originalLines;
        OriginalLineCount = originalLineCount;
    }

    /// <summary>The normalized lines, without terminators.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>For each normalized line, the zero-based line of the original text it shows.</summary>
    public IReadOnlyList<int> OriginalLines => _originalLines;

    /// <summary>Number of lines in the original text.</summary>
    public int OriginalLineCount { get; }

    /// <summary>The normalized lines joined with LF.</summary>
    public string Text => string.Join("\n", _lines);

    /// <summary>The normalized lines as a document that Core can compare.</summary>
    public TextDocument ToDocument()
    {
        var lines = new TextLine[_lines.Length];
        for (var i = 0; i < _lines.Length; i++)
        {
            lines[i] = new TextLine(_lines[i], i == _lines.Length - 1 ? LineEnding.None : LineEnding.Lf);
        }

        return new TextDocument(lines);
    }

    /// <summary>The zero-based original line shown by a normalized line.</summary>
    public int ToOriginalLine(int normalizedLine) => _originalLines[normalizedLine];

    /// <summary>The normalized line that shows an original line, or -1 when the line was dropped.</summary>
    public int ToNormalizedLine(int originalLine)
    {
        if (originalLine < 0 || originalLine >= OriginalLineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(originalLine));
        }

        if (_normalizedLines is null)
        {
            var map = new int[OriginalLineCount];
            for (var i = 0; i < map.Length; i++)
            {
                map[i] = -1;
            }

            for (var i = 0; i < _originalLines.Length; i++)
            {
                map[_originalLines[i]] = i;
            }

            _normalizedLines = map;
        }

        return _normalizedLines[originalLine];
    }

    /// <summary>The normalized content of an original line, or null when the line was dropped.</summary>
    public string? ForOriginalLine(int originalLine)
    {
        var index = ToNormalizedLine(originalLine);
        return index < 0 ? null : _lines[index];
    }
}
