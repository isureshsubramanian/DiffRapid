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

        // Every hunk, ignored ones included: an ignored hunk can still differ in line count, so the left and right
        // positions only stay in step when it is walked like any other change.
        var all = result.Hunks;
        var important = new List<int>();
        for (var index = 0; index < all.Count; index++)
        {
            if (all[index].Significance == DiffSignificance.Important)
            {
                important.Add(index);
            }
        }

        if (important.Count == 0)
        {
            return;
        }

        writer.Write("--- " + leftLabel + "\n");
        writer.Write("+++ " + rightLabel + "\n");

        var i = 0;
        while (i < important.Count)
        {
            // Merge important hunks whose context windows overlap into one output hunk. Ignored hunks between them
            // are written as changes, so the output still turns the left text into the right text.
            var last = i;
            while (last + 1 < important.Count && all[important[last + 1]].LeftStart - all[important[last]].LeftEnd <= 2 * contextLines)
            {
                last++;
            }

            var firstIndex = important[i];
            var lastIndex = important[last];
            var first = all[firstIndex];
            var final = all[lastIndex];

            // Context comes only from the unchanged run next to the window, where left and right lines pair up.
            var previousEnd = firstIndex > 0 ? all[firstIndex - 1].LeftEnd : 0;
            var nextStart = lastIndex + 1 < all.Count ? all[lastIndex + 1].LeftStart : result.Left.LineCount;
            var before = Math.Min(contextLines, first.LeftStart - previousEnd);
            var after = Math.Min(contextLines, nextStart - final.LeftEnd);
            var leftStart = first.LeftStart - before;
            var rightStart = first.RightStart - before;
            var leftEnd = final.LeftEnd + after;
            var rightEnd = final.RightEnd + after;

            writer.Write("@@ -" + Range(leftStart, leftEnd - leftStart) + " +" + Range(rightStart, rightEnd - rightStart) + " @@\n");

            var l = leftStart;
            var r = rightStart;
            for (var h = firstIndex; h <= lastIndex; h++)
            {
                var hunk = all[h];
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
