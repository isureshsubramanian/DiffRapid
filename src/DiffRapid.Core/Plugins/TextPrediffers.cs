using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DiffRapid.Core.Plugins;

/// <summary>Splits text into lines and joins them again, keeping each line's own ending.</summary>
internal static class TextLines
{
    /// <summary>Returns each line's content and its ending ("\r\n", "\n", "\r" or "" for the last line).</summary>
    public static List<(string Content, string Ending)> Split(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character != '\n' && character != '\r')
            {
                continue;
            }

            var ending = character == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : character.ToString();
            lines.Add((text.Substring(start, i - start), ending));
            i += ending.Length - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add((text.Substring(start), string.Empty));
        }

        return lines;
    }

    /// <summary>Applies a function to the content of every line, keeping the line endings.</summary>
    public static string Map(string text, Func<string, string> map)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var (content, ending) in Split(text))
        {
            builder.Append(map(content)).Append(ending);
        }

        return builder.ToString();
    }

    /// <summary>Joins line contents with the first ending found in the original text.</summary>
    public static string Join(IEnumerable<string> contents, string originalText)
    {
        var lines = Split(originalText);
        var newLine = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\n";
        var trailing = lines.Count > 0 && lines[lines.Count - 1].Ending.Length > 0;
        var joined = string.Join(newLine, contents);
        return trailing && joined.Length > 0 ? joined + newLine : joined;
    }
}

/// <summary>Line-by-line prediffers: sorting, case, tabs, regex replacement and line numbers.</summary>
internal static class TextPrediffers
{
    private static readonly Regex LeadingNumber = new(@"^[ \t]*[0-9]+[ \t]*", RegexOptions.CultureInvariant);

    public static string SortLines(string text, PluginContext context) => Sort(text, context, "SortLines", unique: false);

    public static string SortLinesUnique(string text, PluginContext context) => Sort(text, context, "SortLinesUnique", unique: true);

    public static string IgnoreLineNumbers(string text, PluginContext context)
    {
        StepArguments.Parse("IgnoreLineNumbers", context.Arguments).ExpectAtMost(0);
        return TextLines.Map(text, line => LeadingNumber.Replace(line, string.Empty));
    }

    public static string ReplaceRegex(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("ReplaceRegex", context.Arguments, flags: ["ignorecase"]);
        arguments.Expect(2, "a pattern and a replacement");
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (arguments.Has("ignorecase"))
        {
            options |= RegexOptions.IgnoreCase;
        }

        Regex regex;
        try
        {
            regex = new Regex(arguments.Positionals[0], options, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"ReplaceRegex: invalid pattern '{arguments.Positionals[0]}'.", ex);
        }

        try
        {
            return regex.Replace(text, arguments.Positionals[1]);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new PluginException("ReplaceRegex: the pattern took too long to match.", ex);
        }
    }

    public static string Tabify(string text, PluginContext context)
    {
        var tabSize = TabSize("Tabify", context);
        return TextLines.Map(text, line => TabifyLine(line, tabSize));
    }

    public static string Untabify(string text, PluginContext context)
    {
        var tabSize = TabSize("Untabify", context);
        return TextLines.Map(text, line => UntabifyLine(line, tabSize));
    }

    private static string Sort(string text, PluginContext context, string name, bool unique)
    {
        var arguments = StepArguments.Parse(name, context.Arguments, flags: ["desc", "ignorecase"]);
        arguments.ExpectAtMost(0);
        var comparer = arguments.Has("ignorecase") ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        IEnumerable<string> lines = TextLines.Split(text).Select(line => line.Content);
        if (unique)
        {
            lines = lines.Distinct(comparer);
        }

        var sorted = arguments.Has("desc") ? lines.OrderByDescending(line => line, comparer) : lines.OrderBy(line => line, comparer);
        return TextLines.Join(sorted, text);
    }

    private static int TabSize(string name, PluginContext context)
    {
        var arguments = StepArguments.Parse(name, context.Arguments, keys: ["tabsize"]);
        arguments.ExpectAtMost(0);
        return arguments.PositiveInt("tabsize", 4);
    }

    private static string TabifyLine(string line, int tabSize)
    {
        var column = 0;
        var index = 0;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            column = line[index] == '\t' ? (column / tabSize + 1) * tabSize : column + 1;
            index++;
        }

        if (index == 0)
        {
            return line;
        }

        return new string('\t', column / tabSize) + new string(' ', column % tabSize) + line.Substring(index);
    }

    private static string UntabifyLine(string line, int tabSize)
    {
        if (line.IndexOf('\t') < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + tabSize);
        foreach (var character in line)
        {
            if (character == '\t')
            {
                builder.Append(' ', tabSize - builder.Length % tabSize);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
