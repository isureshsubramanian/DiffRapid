using System;
using System.Collections.Generic;
using System.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// Removes comments while keeping the line count: a block comment leaves its line breaks behind, and
/// lines that lost a comment have their trailing spaces trimmed. Quoted strings in C-like, script and
/// SQL code are skipped so that <c>"http://x"</c> is not cut.
/// </summary>
internal static class CommentStripper
{
    private static readonly string[] Styles = ["c", "script", "sql", "xml"];

    public static string Transform(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("IgnoreComments", context.Arguments, flags: Styles);
        arguments.ExpectAtMost(0);
        var syntax = new Syntax(
            CLike: arguments.Has("c"),
            Script: arguments.Has("script"),
            Sql: arguments.Has("sql"),
            Xml: arguments.Has("xml"));
        if (!syntax.CLike && !syntax.Script && !syntax.Sql && !syntax.Xml)
        {
            syntax = syntax with { CLike = true };
        }

        var stripped = Strip(text, syntax);
        return TrimChangedLines(text, stripped);
    }

    private static string Strip(string text, Syntax syntax)
    {
        var output = new StringBuilder(text.Length);
        var trackStrings = syntax.CLike || syntax.Script || syntax.Sql;
        var position = 0;
        while (position < text.Length)
        {
            if (trackStrings && (text[position] == '"' || text[position] == '\''))
            {
                position = CopyString(text, position, output, backslashEscapes: syntax.CLike || syntax.Script);
                continue;
            }

            var lineComment = syntax.CLike && StartsWith(text, position, "//")
                || syntax.Sql && StartsWith(text, position, "--")
                || syntax.Script && text[position] == '#';
            if (lineComment)
            {
                position = SkipToLineEnd(text, position);
                continue;
            }

            if ((syntax.CLike || syntax.Sql) && StartsWith(text, position, "/*"))
            {
                position = SkipBlock(text, position + 2, "*/", output);
                continue;
            }

            if (syntax.Xml && StartsWith(text, position, "<!--"))
            {
                position = SkipBlock(text, position + 4, "-->", output);
                continue;
            }

            output.Append(text[position]);
            position++;
        }

        return output.ToString();
    }

    private static bool StartsWith(string text, int position, string value) =>
        string.CompareOrdinal(text, position, value, 0, value.Length) == 0;

    private static int CopyString(string text, int position, StringBuilder output, bool backslashEscapes)
    {
        var quote = text[position];
        output.Append(quote);
        position++;
        while (position < text.Length)
        {
            var character = text[position];
            if (character is '\n' or '\r')
            {
                // An unterminated string ends at the line end rather than swallowing the file.
                return position;
            }

            output.Append(character);
            position++;
            if (backslashEscapes && character == '\\' && position < text.Length && text[position] is not '\n' and not '\r')
            {
                output.Append(text[position]);
                position++;
            }
            else if (character == quote)
            {
                return position;
            }
        }

        return position;
    }

    private static int SkipToLineEnd(string text, int position)
    {
        while (position < text.Length && text[position] != '\n' && text[position] != '\r')
        {
            position++;
        }

        return position;
    }

    private static int SkipBlock(string text, int position, string terminator, StringBuilder output)
    {
        while (position < text.Length && !StartsWith(text, position, terminator))
        {
            if (text[position] is '\n' or '\r')
            {
                output.Append(text[position]);
            }

            position++;
        }

        return Math.Min(text.Length, position + terminator.Length);
    }

    private static string TrimChangedLines(string original, string stripped)
    {
        var before = TextLines.Split(original);
        var after = TextLines.Split(stripped);
        var builder = new StringBuilder(stripped.Length);
        for (var i = 0; i < after.Count; i++)
        {
            var (content, ending) = after[i];
            var changed = i >= before.Count || before[i].Content != content;
            builder.Append(changed ? content.TrimEnd(' ', '\t') : content).Append(ending);
        }

        return builder.ToString();
    }

    private readonly record struct Syntax(bool CLike, bool Script, bool Sql, bool Xml);
}
