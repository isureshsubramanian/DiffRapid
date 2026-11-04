using System;
using System.Text;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Merge;

/// <summary>The versions and labels extracted from a conflict file.</summary>
public sealed record ConflictFileParts(
    string Left, string Right, string? Base, int ConflictCount,
    string? LeftLabel, string? BaseLabel, string? RightLabel);

/// <summary>Parses git-style conflict markers while preserving content line endings.</summary>
public static class ConflictFile
{
    /// <summary>Extracts versions from a conflict file, rejecting malformed markers.</summary>
    public static ConflictFileParts Parse(string text)
    {
        var document = TextDocument.Parse(text);
        var left = new StringBuilder();
        var right = new StringBuilder();
        var original = new StringBuilder();
        string? leftLabel = null, baseLabel = null, rightLabel = null;
        var count = 0;
        var baseCount = 0;
        var state = Section.Outside;
        var openingLine = 0;
        for (var i = 0; i < document.LineCount; i++)
        {
            var line = document[i];
            var marker = Marker(line.Content);
            switch (marker)
            {
                case '<' when state == Section.Outside:
                    if (count == 0)
                    {
                        leftLabel = Label(line.Content);
                    }

                    count++;
                    openingLine = i + 1;
                    state = Section.Left;
                    break;
                case '|' when state == Section.Left:
                    if (baseCount == 0)
                    {
                        baseLabel = Label(line.Content);
                    }

                    baseCount++;
                    state = Section.Base;
                    break;
                case '=' when state == Section.Left || state == Section.Base:
                    state = Section.Right;
                    break;
                case '>' when state == Section.Right:
                    if (count == 1)
                    {
                        rightLabel = Label(line.Content);
                    }

                    state = Section.Outside;
                    break;
                case '\0':
                    if (state == Section.Outside || state == Section.Left)
                    {
                        left.Append(line.Content).Append(line.EndingText);
                    }

                    if (state == Section.Outside || state == Section.Right)
                    {
                        right.Append(line.Content).Append(line.EndingText);
                    }

                    if (state == Section.Outside || state == Section.Base)
                    {
                        original.Append(line.Content).Append(line.EndingText);
                    }

                    break;
                default:
                    throw new FormatException($"Unexpected conflict marker at line {i + 1}.");
            }
        }

        if (state != Section.Outside)
        {
            throw new FormatException($"Unterminated conflict starting at line {openingLine}.");
        }

        return new ConflictFileParts(left.ToString(), right.ToString(),
            baseCount == count ? original.ToString() : null, count, leftLabel, baseLabel, rightLabel);
    }

    private static char Marker(string content)
    {
        if (content.Length < 7 || (content[0] != '<' && content[0] != '|' && content[0] != '=' && content[0] != '>'))
        {
            return '\0';
        }

        for (var i = 1; i < 7; i++)
        {
            if (content[i] != content[0])
            {
                return '\0';
            }
        }

        return content.Length == 7 || content[7] == ' ' || content[7] == '\t' ? content[0] : '\0';
    }

    private static string? Label(string content)
    {
        var label = content.Substring(7).Trim();
        return label.Length == 0 ? null : label;
    }

    private enum Section
    {
        Outside,
        Left,
        Base,
        Right,
    }
}
