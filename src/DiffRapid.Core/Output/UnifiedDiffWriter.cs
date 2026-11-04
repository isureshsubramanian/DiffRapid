using System;
using System.Collections.Generic;
using System.IO;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Output;

/// <summary>Writes a comparison as a unified diff (the format used by <c>diff -u</c> and git).</summary>
public static class UnifiedDiffWriter
{
    public static string Write(TextDiffResult result, string leftLabel, string rightLabel, int contextLines = 3)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        Write(writer, result, leftLabel, rightLabel, contextLines);
        return writer.ToString();
    }

    public static void Write(TextWriter writer, TextDiffResult result, string leftLabel, string rightLabel, int contextLines = 3)
    {
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines));
        }

        var hunks = new List<DiffHunk>(result.ImportantHunks);
        if (hunks.Count == 0)
        {
            return;
        }

        writer.Write("--- " + leftLabel + "\n");
        writer.Write("+++ " + rightLabel + "\n");

        var i = 0;
        while (i < hunks.Count)
        {
            // Merge hunks whose context windows overlap into one output hunk.
            var first = i;
            var last = i;
            while (last + 1 < hunks.Count && hunks[last + 1].LeftStart - hunks[last].LeftEnd <= 2 * contextLines)
            {
                last++;
            }

            var leftStart = Math.Max(0, hunks[first].LeftStart - contextLines);
            var rightStart = Math.Max(0, hunks[first].RightStart - contextLines);
            var leftEnd = Math.Min(result.Left.LineCount, hunks[last].LeftEnd + contextLines);
            var rightEnd = Math.Min(result.Right.LineCount, hunks[last].RightEnd + contextLines);

            writer.Write("@@ -" + Range(leftStart, leftEnd - leftStart) + " +" + Range(rightStart, rightEnd - rightStart) + " @@\n");

            var l = leftStart;
            var r = rightStart;
            for (var h = first; h <= last; h++)
            {
                var hunk = hunks[h];
                while (l < hunk.LeftStart)
                {
                    WriteLine(writer, ' ', result.Left, l++);
                    r++;
                }

                for (; l < hunk.LeftEnd; l++)
                {
                    WriteLine(writer, '-', result.Left, l);
                }

                for (; r < hunk.RightEnd; r++)
                {
                    WriteLine(writer, '+', result.Right, r);
                }
            }

            while (l < leftEnd)
            {
                WriteLine(writer, ' ', result.Left, l++);
            }

            i = last + 1;
        }
    }

    private static string Range(int start, int count)
    {
        // Unified format is one-based; an empty range points at the line before it.
        var displayStart = count == 0 ? start : start + 1;
        return count == 1 ? displayStart.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : displayStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteLine(TextWriter writer, char prefix, TextDocument doc, int index)
    {
        var line = doc[index];
        writer.Write(prefix);
        writer.Write(line.Content);
        writer.Write('\n');
        if (line.Ending == LineEnding.None)
        {
            writer.Write("\\ No newline at end of file\n");
        }
    }
}
